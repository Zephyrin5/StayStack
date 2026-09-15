using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using BuildingBlocks.Persistence;
using Dapper;
using Hosts;
using Hosts.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
namespace IntegrationTests.Features.Persistence;

// IAtomicScope's contract against real Postgres: one transaction across two
// modules' contexts, with an EF write through the owner (Hosts), and an EF write and
// a Dapper write through a borrower (Bookings). The borrower's EF obligation id is
// derived from the Dapper one, so both are checked by one id.
[Collection("Integration Tests")]
public class AtomicScopeTests(IntegrationTestWebApplicationFactory factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const AtomicParticipants Owner = AtomicParticipants.Hosts;
    private const AtomicParticipants Both = AtomicParticipants.Hosts | AtomicParticipants.Bookings;

    private sealed class Boom() : Exception("thrown inside the scope");

    private static async Task WriteBothAsync(IServiceProvider services, Guid hostId, Guid obligationId)
    {
        AppHostsDbContext hosts = services.GetRequiredService<AppHostsDbContext>();
        hosts.Hosts.Add(Host.Create(hostId, "Atomic Scope Host", "scope@example.com", null));
        await hosts.SaveChangesAsync(Ct);

        AppBookingsDbContext bookings = services.GetRequiredService<AppBookingsDbContext>();
        bookings.RefundObligations.Add(new RefundObligation
        {
            BookingId = EfObligationId(obligationId),
            CancelledAt = DateTimeOffset.UtcNow,
            PolicyRefundAmount = 1m,
            Currency = SeedWork.Enums.Currency.KWD,
            Cause = BookingCancellationCause.Expiry,
            NextAttemptAt = DateTimeOffset.UtcNow
        });
        await bookings.SaveChangesAsync(Ct);

        await bookings.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO refund_obligations (booking_id, attempts, cancelled_at, cause, currency, next_attempt_at, policy_refund_amount)
            VALUES (@Id, 0, now(), 'Expiry', 'KWD', now(), 1)
            """,
            new { Id = obligationId }, bookings.Database.CurrentTransaction!.GetDbTransaction(), cancellationToken: Ct));
    }

    private static Guid EfObligationId(Guid obligationId)
    {
        byte[] bytes = obligationId.ToByteArray();
        bytes[15] ^= 0xFF;
        return new Guid(bytes);
    }

    private async Task<(long Hosts, long Obligations)> CountCommittedAsync(Guid hostId, Guid obligationId)
    {
        await using NpgsqlConnection connection = new NpgsqlConnection(factory.ConnectionString);
        await connection.OpenAsync(Ct);
        long hosts = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM hosts WHERE id = @hostId", new { hostId });
        long obligations = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM refund_obligations WHERE booking_id IN (@obligationId, @efId)",
            new { obligationId, efId = EfObligationId(obligationId) });
        return (hosts, obligations);
    }

    private static async Task AssertWorksOnItsOwnConnectionAsync(DbContext context, DbContext other)
    {
        Assert.Null(context.Database.CurrentTransaction);
        Assert.Equal(System.Data.ConnectionState.Closed, context.Database.GetDbConnection().State);
        Assert.NotSame(other.Database.GetDbConnection(), context.Database.GetDbConnection());
        Assert.False(string.IsNullOrEmpty(context.Database.GetConnectionString()));

        // A real round trip, outside any scope.
        Assert.Equal(1, await context.Database.SqlQueryRaw<int>("SELECT 1 AS \"Value\"").SingleAsync(Ct));
    }

    [Fact]
    public async Task AFailureInsideTheScope_RollsBackBothModules_AndLeavesEveryParticipantUsable()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        Guid hostId = Guid.CreateVersion7(), obligationId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<Boom>(() => services.GetRequiredService<IAtomicScope>().ExecuteAsync(Owner, Both, async _ =>
        {
            await WriteBothAsync(services, hostId, obligationId);
            throw new Boom();
        }, Ct));

        Assert.Equal((0L, 0L), await CountCommittedAsync(hostId, obligationId));

        AppHostsDbContext hosts = services.GetRequiredService<AppHostsDbContext>();
        AppBookingsDbContext bookings = services.GetRequiredService<AppBookingsDbContext>();

        // Nothing from the failed attempt is left tracked: its entities were saved,
        // so they would read as Unchanged while describing rows that do not exist.
        Assert.Empty(hosts.ChangeTracker.Entries());
        Assert.Empty(bookings.ChangeTracker.Entries());

        // Restoration ran on the failure path: each context is back on its own connection.
        await AssertWorksOnItsOwnConnectionAsync(bookings, hosts);
        await AssertWorksOnItsOwnConnectionAsync(hosts, bookings);
    }

    [Fact]
    public async Task ATransientFailureAfterPartialWork_RetriesTheWholeScope_WithoutDuplicatesOrStaleEntities()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        Guid hostId = Guid.CreateVersion7(), obligationId = Guid.CreateVersion7();
        int attempts = 0;

        await services.GetRequiredService<IAtomicScope>().ExecuteAsync(Owner, Both, async _ =>
        {
            attempts++;
            await WriteBothAsync(services, hostId, obligationId);

            if (attempts == 1)
            {
                // Transient by the execution strategy's configuration.
                throw new PostgresException("injected", "ERROR", "ERROR", "40001");
            }
        }, Ct);

        Assert.Equal(2, attempts);
        Assert.Equal((1L, 2L), await CountCommittedAsync(hostId, obligationId));

        // Attempt one's Added host was cleared, not carried: only attempt two's saved
        // instance is tracked, and nothing is pending.
        AppHostsDbContext hosts = services.GetRequiredService<AppHostsDbContext>();
        Assert.Single(hosts.ChangeTracker.Entries<Host>());
        Assert.False(hosts.ChangeTracker.HasChanges());
        Assert.Single(services.GetRequiredService<AppBookingsDbContext>().ChangeTracker.Entries<RefundObligation>());
        await AssertWorksOnItsOwnConnectionAsync(services.GetRequiredService<AppBookingsDbContext>(), hosts);
    }

    [Fact]
    public async Task AParticipantWithUnsavedChanges_IsRefusedByName_BeforeAnythingRuns()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        services.GetRequiredService<AppBookingsDbContext>().RefundObligations.Add(new RefundObligation
        {
            BookingId = Guid.CreateVersion7(),
            CancelledAt = DateTimeOffset.UtcNow,
            PolicyRefundAmount = 1m,
            Currency = SeedWork.Enums.Currency.KWD,
            Cause = BookingCancellationCause.Expiry,
            NextAttemptAt = DateTimeOffset.UtcNow
        });
        bool ran = false;

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<IAtomicScope>().ExecuteAsync(Owner, Both, _ =>
            {
                ran = true;
                return Task.CompletedTask;
            }, Ct));

        Assert.False(ran);
        Assert.Contains(nameof(AppBookingsDbContext), refused.Message);
        Assert.Contains("unsaved", refused.Message);
    }

    [Fact]
    public async Task AParticipantAlreadyInATransaction_IsRefusedByName()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        AppBookingsDbContext bookings = services.GetRequiredService<AppBookingsDbContext>();
        await using IDbContextTransaction open = await bookings.Database.BeginTransactionAsync(Ct);

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<IAtomicScope>().ExecuteAsync(Owner, Both, _ => Task.CompletedTask, Ct));

        Assert.Contains(nameof(AppBookingsDbContext), refused.Message);
        Assert.Contains("already in a transaction", refused.Message);
    }

    [Fact]
    public async Task CommitFaults_OnTheOwnersContext_FireOnTheScopesCommit()
    {
        // Fault injection hooks EF transaction interception. The scope commits the
        // owner's EF transaction, so a fault on the owner's context must still fire;
        // if it did not, every ambiguity test converted to a scope would silently
        // stop testing anything.
        Guid hostId = Guid.CreateVersion7(), obligationId = Guid.CreateVersion7();
        CommitFault<AppHostsDbContext> beforeCommit = CommitFaults.FailBeforeCommit<AppHostsDbContext>(
            hosts => hosts.ChangeTracker.Entries<Host>().Any(e => e.Entity.Id == hostId));
        await using WebApplicationFactory<Program> host = factory.WithCommitFault(beforeCommit);

        using IServiceScope scope = host.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        int attempts = 0;

        await services.GetRequiredService<IAtomicScope>().ExecuteAsync(Owner, Both, async _ =>
        {
            attempts++;
            await WriteBothAsync(services, hostId, obligationId);
        }, Ct);

        Assert.True(beforeCommit.HasFired, "The owner's commit fault never fired inside the scope.");
        Assert.Equal(2, attempts);
        Assert.Equal((1L, 2L), await CountCommittedAsync(hostId, obligationId));
    }

    [Fact]
    public async Task CommitFaults_OnABorrowersContext_DoNotFire()
    {
        // The borrower enlists in the owner's transaction and never commits one of
        // its own, so a fault registered on its context has nothing to hook. Pinned
        // so a converted test cannot target a borrower and pass vacuously.
        Guid hostId = Guid.CreateVersion7(), obligationId = Guid.CreateVersion7();
        CommitFault<AppBookingsDbContext> borrowerFault = CommitFaults.FailBeforeCommit<AppBookingsDbContext>(_ => true);
        await using WebApplicationFactory<Program> host = factory.WithCommitFault(borrowerFault);

        using IServiceScope scope = host.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;

        await services.GetRequiredService<IAtomicScope>().ExecuteAsync(Owner, Both, _ =>
            WriteBothAsync(services, hostId, obligationId), Ct);

        Assert.False(borrowerFault.HasFired);
        Assert.Equal((1L, 2L), await CountCommittedAsync(hostId, obligationId));
    }
}
