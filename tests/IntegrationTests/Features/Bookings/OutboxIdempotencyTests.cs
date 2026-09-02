using Bookings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Outbox;
namespace IntegrationTests.Features.Bookings;

// OutboxDispatcherBase requires TryHandleAsync to be idempotent. That is easy
// to read as boilerplate, so this makes it observable: a single logical
// message whose handler's side effect runs twice, with no concurrency
// involved and nothing misconfigured.
//
// The sibling OutboxDispatcherConcurrencyTests proves the row lock gives
// mutual exclusion between overlapping dispatchers. These two are not the same
// guarantee, and conflating them is the mistake this file exists to prevent:
// the lock stops two dispatchers running at once, and does nothing about one
// dispatcher running the same handler twice in sequence.
[Collection("Integration Tests")]
public class OutboxIdempotencyTests(IntegrationTestWebApplicationFactory factory)
{
    // Performs its side effect and only then fails, which is the shape that
    // matters: the effect is already out in the world when the row's outcome
    // fails to record. A handler that failed *before* its side effect would
    // be unremarkable - retrying that is the whole point of a retry.
    private sealed class SideEffectThenFailDispatcher(
        AppBookingsDbContext dbContext, TimeProvider timeProvider, ILogger<SideEffectThenFailDispatcher> logger)
        : OutboxDispatcherBase<AppBookingsDbContext>(dbContext, timeProvider, logger)
    {
        public int SideEffectCount;
        public bool FailAfterSideEffect = true;

        protected override string ModuleName => "IdempotencyTest";

        protected override Task TryHandleAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            // The "real" work - the part that reaches another module and
            // cannot be undone by rolling back this table's row.
            SideEffectCount++;

            if (FailAfterSideEffect)
            {
                FailAfterSideEffect = false;
                throw new InvalidOperationException("Outcome failed to record after the side effect landed.");
            }

            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task Dispatch_WhenTheOutcomeFailsToRecordAfterTheSideEffectLands_RunsTheSideEffectAgain()
    {
        Guid messageId = Guid.CreateVersion7();

        using (IServiceScope seedScope = factory.Services.CreateScope())
        {
            AppBookingsDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            seedDb.BookingsOutboxMessages.Add(new OutboxMessage
            {
                Id = messageId,
                Type = "IdempotencyTestMessage",
                Payload = "{}",
                CreatedAt = DateTimeOffset.UtcNow,
                NextAttemptAt = DateTimeOffset.UtcNow
            });
            await seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        SideEffectThenFailDispatcher dispatcher = new SideEffectThenFailDispatcher(
            dbContext,
            scope.ServiceProvider.GetRequiredService<TimeProvider>(),
            scope.ServiceProvider.GetRequiredService<ILogger<SideEffectThenFailDispatcher>>());

        // TryDispatchAsync, not DispatchPendingAsync: the latter claims
        // whatever is pending in the shared test database, so this dispatcher
        // would also handle rows other tests left behind and the count below
        // would measure them too. Claiming by id scopes it to this message.
        OutboxMessage seeded = await ReadAsync(messageId);

        // First dispatch: the side effect lands, then recording it fails.
        await dispatcher.TryDispatchAsync(seeded, TestContext.Current.CancellationToken);

        Assert.Equal(1, dispatcher.SideEffectCount);

        OutboxMessage afterFirst = await ReadAsync(messageId);
        Assert.Null(afterFirst.ProcessedAt);
        Assert.Equal(1, afterFirst.Attempts);

        // Second dispatch, exactly what the relay job does when it comes back
        // after the backoff elapses.
        await dispatcher.TryDispatchAsync(seeded, TestContext.Current.CancellationToken);

        // The point of the whole file: ONE message, TWO side effects. Nothing
        // here is concurrent and nothing is misconfigured - this is the
        // dispatcher working as designed. If the side effect were a refund
        // rather than a counter, that is two refunds.
        Assert.Equal(2, dispatcher.SideEffectCount);

        OutboxMessage afterSecond = await ReadAsync(messageId);
        Assert.NotNull(afterSecond.ProcessedAt);
    }

    private async Task<OutboxMessage> ReadAsync(Guid messageId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        return await dbContext.BookingsOutboxMessages
            .AsNoTracking()
            .SingleAsync(m => m.Id == messageId, TestContext.Current.CancellationToken);
    }
}
