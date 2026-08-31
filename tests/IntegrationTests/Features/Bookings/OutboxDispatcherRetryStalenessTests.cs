using Bookings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Outbox;
namespace IntegrationTests.Features.Bookings;

// Proves ClaimAndDispatchAsync reads the row's real database state even
// when the same DbContext already tracks a stale, mutated-but-never-saved
// copy of it - the shape a transient-failure retry leaves behind (a rolled
// back transaction undoes nothing in EF's own change tracker, and the
// execution strategy re-runs ClaimAndDispatchAsync's whole lambda, including
// the claim query, against the same DbContext instance). Without
// DbContext.ChangeTracker.Clear() at the top of that lambda, EF's identity
// resolution hands the claim query back the already-tracked stale instance
// instead of materializing the row's current values, so a message that was
// never actually processed reads as already-processed and is silently
// skipped forever.
[Collection("Integration Tests")]
public class OutboxDispatcherRetryStalenessTests(IntegrationTestWebApplicationFactory factory)
{
    private sealed class CountingDispatcher(AppBookingsDbContext dbContext)
        : OutboxDispatcherBase<AppBookingsDbContext>(dbContext, TimeProvider.System, NullLogger<CountingDispatcher>.Instance)
    {
        public int HandledCount;

        protected override string ModuleName => "Test";

        protected override Task TryHandleAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            HandledCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task TryDispatchAsync_WhenTheContextAlreadyTracksAStaleCopyOfTheRow_StillProcessesTheRealRow()
    {
        // Arrange - a genuinely unprocessed row.
        Guid messageId = Guid.CreateVersion7();
        OutboxMessage seedMessage = new OutboxMessage
        {
            Id = messageId,
            Type = "TestMessage",
            Payload = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow
        };

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        dbContext.BookingsOutboxMessages.Add(seedMessage);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Simulate exactly what a transient-failure retry leaves behind:
        // an earlier attempt on this same DbContext instance mutated the
        // tracked row (ProcessedAt set) and never committed. Loaded via a
        // plain tracking query outside any explicit transaction, so this
        // mutation is purely in-memory and never reaches the database -
        // the same end state a rolled-back BeginTransactionAsync/
        // CommitAsync would leave the change tracker in.
        OutboxMessage staleTrackedCopy = await dbContext.BookingsOutboxMessages
            .SingleAsync(m => m.Id == messageId, TestContext.Current.CancellationToken);
        staleTrackedCopy.ProcessedAt = DateTimeOffset.UtcNow;

        CountingDispatcher dispatcher = new CountingDispatcher(dbContext);

        // Act - a fresh dispatch attempt against the same DbContext, the
        // same shape as an execution-strategy retry re-running
        // ClaimAndDispatchAsync's lambda from the top after the first
        // attempt's commit failed transiently.
        await dispatcher.TryDispatchAsync(staleTrackedCopy, TestContext.Current.CancellationToken);

        // Assert - the row's real, current database state (ProcessedAt
        // still null) was consulted and actually handled, not shadowed by
        // the stale in-memory ProcessedAt left over from the "earlier
        // attempt". Without DbContext.ChangeTracker.Clear() at the top of
        // the retried delegate, the claim query's identity resolution
        // would hand back the stale tracked instance, the
        // already-non-null ProcessedAt guard would treat this row as
        // already claimed by someone else, TryHandleAsync would never
        // run, and the row would stay unprocessed in the database forever
        // despite genuinely never having been handled.
        Assert.Equal(1, dispatcher.HandledCount);

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppBookingsDbContext assertDb = assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        OutboxMessage persisted = await assertDb.BookingsOutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Id == messageId, TestContext.Current.CancellationToken);
        Assert.NotNull(persisted.ProcessedAt);
    }
}
