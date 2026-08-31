using Bookings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Outbox;
namespace IntegrationTests.Features.Bookings;

// Proves FOR UPDATE SKIP LOCKED (OutboxDispatcherBase.ClaimAndDispatchAsync)
// actually provides mutual exclusion under real concurrent contention - a
// single-threaded test can't exercise this at all, and every current
// message type (ReleaseHoldAsync, ReverseRedemptionAsync, etc.) happens to
// be idempotent, which would mask a broken/absent lock entirely: a
// double-dispatch against an idempotent action doesn't corrupt end state,
// it just silently runs the same action twice. See docs/adr/0003 and
// OutboxDispatcherBase's own class doc comment.
//
// Uses a purpose-built counting dispatcher rather than a real message type
// specifically so a double-claim is directly observable as "handled more
// than once", not something that has to be inferred from idempotent end
// state the way it would with the app's own real handlers.
[Collection("Integration Tests")]
public class OutboxDispatcherConcurrencyTests(IntegrationTestWebApplicationFactory factory)
{
    private sealed class CountingOutboxDispatcher(AppBookingsDbContext dbContext, TimeProvider timeProvider, ILogger<CountingOutboxDispatcher> logger)
        : OutboxDispatcherBase<AppBookingsDbContext>(dbContext, timeProvider, logger)
    {
        public int HandledCount;

        protected override string ModuleName => "ConcurrencyTest";

        protected override async Task TryHandleAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref HandledCount);

            // Widens the race window deliberately - without holding the
            // claim for a moment, a fast local Postgres instance could
            // resolve every concurrent claim query before any of them
            // actually overlap in time, and the test would pass even with
            // no locking at all.
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }
    }

    [Fact]
    public async Task DispatchPendingAsync_ConcurrentOverlappingRuns_HandleTheSameRowExactlyOnce()
    {
        // Arrange - one unprocessed row, simulating two overlapping runs of
        // the same one-minute relay cron (a batch slower than its own
        // cadence), or two app instances polling at once.
        Guid messageId = Guid.CreateVersion7();

        using (IServiceScope seedScope = factory.Services.CreateScope())
        {
            AppBookingsDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            seedDb.BookingsOutboxMessages.Add(new OutboxMessage
            {
                Id = messageId,
                Type = "ConcurrencyTestMessage",
                Payload = "{}",
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            });
            await seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act - several concurrent "relay runs", each its own DI scope (own
        // DbContext, own connection - a shared DbContext would serialize
        // these onto one connection and never actually race at the
        // database), all scanning for and trying to claim the same row.
        int totalHandled = 0;
        const int concurrentRuns = 8;

        async Task RunOnceAsync()
        {
            using IServiceScope scope = factory.Services.CreateScope();
            AppBookingsDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            CountingOutboxDispatcher dispatcher = new CountingOutboxDispatcher(
                dbContext, TimeProvider.System, NullLogger<CountingOutboxDispatcher>.Instance);

            await dispatcher.DispatchPendingAsync(50, TestContext.Current.CancellationToken);

            Interlocked.Add(ref totalHandled, dispatcher.HandledCount);
        }

        await Task.WhenAll(Enumerable.Range(0, concurrentRuns).Select(_ => RunOnceAsync()));

        // Assert - exactly one of the eight concurrent runs actually
        // handled the row, not eight (no lock at all) and not zero
        // (something claimed it but never processed it).
        Assert.Equal(1, totalHandled);

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppBookingsDbContext assertDb = assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        OutboxMessage persisted = await assertDb.BookingsOutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Id == messageId, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted.ProcessedAt);
        Assert.Equal(0, persisted.Attempts);
    }
}
