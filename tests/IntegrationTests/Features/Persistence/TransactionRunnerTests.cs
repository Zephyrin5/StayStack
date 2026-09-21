using Bookings.Contracts;
using Bookings.Entities;
using BuildingBlocks.Persistence;
using Dapper;
using Hosts.Entities;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Persistence;
using SeedWork.Enums;
using System.Data;
namespace IntegrationTests.Features.Persistence;

// The runner's contract against real Postgres: one transaction over two modules' tables, with an EF
// write and a Dapper write, retried as a unit. The Dapper obligation's id is derived from the EF one,
// so both are checked by one id.
[Collection(CommonCollection.Name)]
public class TransactionRunnerTests(CommonFixture factory)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private sealed class Boom() : Exception("thrown inside the transaction");

    private static async Task WriteBothAsync(IServiceProvider services, Guid hostId, Guid obligationId)
    {
        global::Hosts.HostsDb hosts = services.GetRequiredService<global::Hosts.HostsDb>();
        hosts.Hosts.Add(Host.Create(hostId, "Runner Host", "runner@example.com", null));
        await hosts.SaveChangesAsync(Ct);

        global::Bookings.BookingsDb bookings = services.GetRequiredService<global::Bookings.BookingsDb>();
        bookings.RefundObligations.Add(new RefundObligation
        {
            BookingId = EfObligationId(obligationId),
            CancelledAt = DateTimeOffset.UtcNow,
            PolicyRefundAmount = 1m,
            Currency = Currency.KWD,
            Cause = BookingCancellationCause.Expiry,
            NextAttemptAt = DateTimeOffset.UtcNow
        });
        await bookings.SaveChangesAsync(Ct);

        await bookings.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO bookings.refund_obligations (booking_id, attempts, cancelled_at, cause, currency, next_attempt_at, policy_refund_amount)
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
        long hosts = await connection.ExecuteScalarAsync<long>("SELECT count(*) FROM hosts.hosts WHERE id = @hostId", new { hostId });
        long obligations = await connection.ExecuteScalarAsync<long>(
            "SELECT count(*) FROM bookings.refund_obligations WHERE booking_id IN (@obligationId, @efId)",
            new { obligationId, efId = EfObligationId(obligationId) });
        return (hosts, obligations);
    }

    [Fact]
    public async Task AFailureInsideTheTransaction_RollsBackEveryModule_AndLeavesNothingTracked()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        Guid hostId = Guid.CreateVersion7(), obligationId = Guid.CreateVersion7();

        await Assert.ThrowsAsync<Boom>(() =>
            services.GetRequiredService<ITransactionRunner>().ExecuteAsync(IsolationLevel.ReadCommitted, async _ =>
            {
                await WriteBothAsync(services, hostId, obligationId);
                throw new Boom();
            }, Ct));

        Assert.Equal((0L, 0L), await CountCommittedAsync(hostId, obligationId));

        // Saved-then-rolled-back entities read as Unchanged while describing rows that do not exist.
        Assert.Empty(services.GetRequiredService<AppDbContext>().ChangeTracker.Entries());
    }

    [Fact]
    public async Task ATransientFailureAfterPartialWork_RetriesTheWholeTransaction_WithoutDuplicatesOrStaleEntities()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        Guid hostId = Guid.CreateVersion7(), obligationId = Guid.CreateVersion7();
        int attempts = 0;

        await services.GetRequiredService<ITransactionRunner>().ExecuteAsync(IsolationLevel.ReadCommitted, async _ =>
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

        // Attempt one's Added host was cleared rather than carried into attempt two.
        AppDbContext db = services.GetRequiredService<AppDbContext>();
        Assert.Single(db.ChangeTracker.Entries<Host>());
        Assert.Single(db.ChangeTracker.Entries<RefundObligation>());
        Assert.False(db.ChangeTracker.HasChanges());
    }

    [Fact]
    public async Task WorkThatLeavesUnsavedChanges_IsRefusedRatherThanCommitted()
    {
        using IServiceScope scope = factory.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        Guid hostId = Guid.CreateVersion7();

        InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            services.GetRequiredService<ITransactionRunner>().ExecuteAsync(IsolationLevel.ReadCommitted, _ =>
            {
                services.GetRequiredService<global::Hosts.HostsDb>().Hosts.Add(
                    Host.Create(hostId, "Unsaved Host", "unsaved@example.com", null));
                return Task.CompletedTask;
            }, Ct));

        Assert.Contains("unsaved changes", refused.Message);
        Assert.Equal(0L, (await CountCommittedAsync(hostId, Guid.CreateVersion7())).Hosts);
    }

    [Fact]
    public async Task CommitFaults_FireOnTheRunnersCommit_AndTheRetryCommitsOnce()
    {
        // Fault injection hooks EF transaction interception, and the runner commits an EF transaction.
        // If it did not fire, every ambiguity test that runs inside the runner would silently stop
        // testing anything - and would pass, because the failure it injects would never arrive.
        //
        // The fault throws 40001, so the strategy inside the runner absorbs it: the delegate runs a
        // second time and one host is committed. Both halves matter - that the fault reached the
        // commit, and that the attempt it rolled back left nothing behind.
        Guid hostId = Guid.CreateVersion7();
        CommitFault<AppDbContext> beforeCommit = CommitFaults.FailBeforeCommit<AppDbContext>(
            db => db.ChangeTracker.Entries<Host>().Any(e => e.Entity.Id == hostId));
        await using WebApplicationFactory<Program> host = factory.WithCommitFault(beforeCommit);

        using IServiceScope scope = host.Services.CreateScope();
        IServiceProvider services = scope.ServiceProvider;
        int attempts = 0;

        await services.GetRequiredService<ITransactionRunner>().ExecuteAsync(IsolationLevel.ReadCommitted, async _ =>
        {
            attempts++;
            global::Hosts.HostsDb hosts = services.GetRequiredService<global::Hosts.HostsDb>();
            hosts.Hosts.Add(Host.Create(hostId, "Faulted Host", "faulted@example.com", null));
            await hosts.SaveChangesAsync(Ct);
        }, Ct);

        Assert.True(beforeCommit.HasFired, "The commit fault never reached the runner's commit.");
        Assert.Equal(2, attempts);
        Assert.Equal(1L, (await CountCommittedAsync(hostId, Guid.CreateVersion7())).Hosts);
    }
}
