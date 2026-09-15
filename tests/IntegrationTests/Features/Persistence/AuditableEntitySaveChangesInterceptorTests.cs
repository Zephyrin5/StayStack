using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using BuildingBlocks.Identity;
using Hosts;
using Hosts.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Persistence;
using Persistence.Interceptors;
using SeedWork.Enums;
namespace IntegrationTests.Features.Persistence;

// The interceptor on a context of its own, against the migrated schema, so each test
// controls the user and the clock it sees.
[Collection("Integration Tests")]
public class AuditableEntitySaveChangesInterceptorTests(IntegrationTestWebApplicationFactory factory)
{
    private static readonly DateTimeOffset FixedTime = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private sealed class User(Guid? id) : ICurrentUserProvider
    {
        public int Reads { get; private set; }

        public Guid? UserId
        {
            get
            {
                Reads++;
                return id;
            }
        }

        public Guid? HostId => null;
        public IReadOnlyCollection<string> Roles => [];
    }

    private sealed class CountingClock(DateTimeOffset now) : TimeProvider
    {
        public int Reads { get; private set; }

        public override DateTimeOffset GetUtcNow()
        {
            Reads++;
            return now;
        }
    }

    private TContext Create<TContext>(Func<DbContextOptions<TContext>, TContext> create, ICurrentUserProvider user, TimeProvider clock)
        where TContext : DbContext
    {
        DbContextOptionsBuilder<TContext> options = new DbContextOptionsBuilder<TContext>();
        options.ConfigureStayStackDefaults(factory.ConnectionString, "audit_tests", isDevelopment: false);
        options.AddInterceptors(new AuditableEntitySaveChangesInterceptor(user, clock));
        return create(options.Options);
    }

    private static Host NewHost() => Host.Create(Guid.CreateVersion7(), "Audited Host", "audit@example.com", null);

    [Fact]
    public async Task SavingChangesAsync_WithNoUser_StampsTheTimeAndNoCreator()
    {
        await using AppHostsDbContext context = Create<AppHostsDbContext>(o => new AppHostsDbContext(o), new User(null), new FakeTimeProvider(FixedTime));
        Host host = NewHost();

        context.Hosts.Add(host);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(FixedTime, host.CreatedAt);
        Assert.Null(host.CreatedBy);
    }

    [Fact]
    public async Task SavingChangesAsync_IgnoresEntitiesThatAreNotAuditable()
    {
        User user = new User(Guid.NewGuid());
        CountingClock clock = new CountingClock(FixedTime);
        await using AppBookingsDbContext context = Create<AppBookingsDbContext>(o => new AppBookingsDbContext(o), user, clock);

        context.RefundObligations.Add(new RefundObligation
        {
            BookingId = Guid.CreateVersion7(),
            CancelledAt = DateTimeOffset.UtcNow,
            PolicyRefundAmount = 1m,
            Currency = Currency.KWD,
            Cause = BookingCancellationCause.Expiry,
            NextAttemptAt = DateTimeOffset.UtcNow
        });

        Assert.Equal(1, await context.SaveChangesAsync(TestContext.Current.CancellationToken));
        Assert.Equal(0, user.Reads);
        Assert.Equal(0, clock.Reads);
    }

    [Fact]
    public async Task SavingChangesAsync_WithNothingChanged_LeavesTheModificationStampsEmpty()
    {
        await using AppHostsDbContext context = Create<AppHostsDbContext>(o => new AppHostsDbContext(o), new User(Guid.NewGuid()), new FakeTimeProvider(FixedTime));
        Host host = NewHost();
        context.Hosts.Add(host);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        await context.SaveChangesAsync(TestContext.Current.CancellationToken);

        Assert.Null(host.ModifiedAt);
        Assert.Null(host.ModifiedBy);
    }

    [Fact]
    public void SavingChanges_Synchronous_StampsTheTimeAndCreator()
    {
        Guid userId = Guid.NewGuid();
        using AppHostsDbContext context = Create<AppHostsDbContext>(o => new AppHostsDbContext(o), new User(userId), new FakeTimeProvider(FixedTime));
        Host host = NewHost();

        context.Hosts.Add(host);
        context.SaveChanges();

        Assert.Equal(FixedTime, host.CreatedAt);
        Assert.Equal(userId, host.CreatedBy);
    }
}
