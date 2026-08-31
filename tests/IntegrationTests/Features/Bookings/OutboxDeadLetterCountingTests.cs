using Bookings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Outbox;
namespace IntegrationTests.Features.Bookings;

// Proves a permanently-broken message dead-letters exactly once per genuine
// occurrence, not once per hourly sweep retry - Attempts is never reset (see
// SweepDeadLetteredAsync's own doc comment), so a naive re-check of
// "Attempts >= MaxAttempts" after every failed retry would fire the counter
// and OnDeadLetteredAsync hook again on every single sweep, forever, for one
// stuck message. That would make the counter a rate of retries rather than
// a count of distinct dead letters - exactly the thing an alert threshold on
// it needs to not be true.
[Collection("Integration Tests")]
public class OutboxDeadLetterCountingTests(IntegrationTestWebApplicationFactory factory)
{
    private sealed class AlwaysFailingDispatcher(AppBookingsDbContext dbContext)
        : OutboxDispatcherBase<AppBookingsDbContext>(dbContext, TimeProvider.System, NullLogger<AlwaysFailingDispatcher>.Instance)
    {
        public int DeadLetteredHookCallCount;

        protected override string ModuleName => "Test";

        protected override Task TryHandleAsync(OutboxMessage message, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Permanently broken - simulates a message whose failure is never transient.");

        protected override Task OnDeadLetteredAsync(OutboxMessage message, CancellationToken cancellationToken)
        {
            DeadLetteredHookCallCount++;
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task SweepDeadLetteredAsync_RetryingAnAlreadyDeadLetteredMessage_DoesNotFireTheHookAgain()
    {
        // Arrange - one attempt short of MaxAttempts (10) and not yet
        // dead-lettered - the next failure is the genuine, first-ever
        // crossing into dead-letter state.
        Guid messageId = Guid.CreateVersion7();
        OutboxMessage seedMessage = new OutboxMessage
        {
            Id = messageId,
            Type = "AlwaysFailingMessage",
            Payload = "{}",
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
            NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Attempts = 9,
            LastError = "Simulated persistent failure."
        };

        using (IServiceScope seedScope = factory.Services.CreateScope())
        {
            AppBookingsDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            seedDb.BookingsOutboxMessages.Add(seedMessage);
            await seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        // Act 1 - the failure that pushes Attempts from 9 to 10: the first,
        // genuine dead-letter transition. Goes through TryDispatchAsync
        // (the normal/fast-loop path), not the sweep - this is how a
        // message actually becomes dead-lettered the first time.
        using (IServiceScope firstScope = factory.Services.CreateScope())
        {
            AppBookingsDbContext firstDb = firstScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            AlwaysFailingDispatcher firstDispatcher = new AlwaysFailingDispatcher(firstDb);
            await firstDispatcher.TryDispatchAsync(seedMessage, TestContext.Current.CancellationToken);

            Assert.Equal(1, firstDispatcher.DeadLetteredHookCallCount);
        }

        // Act 2 - the hourly sweep retries it, fails again (Attempts 10 ->
        // 11). This is the case the bug report was about: without the fix,
        // this would fire the hook (and the counter) a second time for the
        // same underlying problem.
        using (IServiceScope secondScope = factory.Services.CreateScope())
        {
            AppBookingsDbContext secondDb = secondScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            AlwaysFailingDispatcher secondDispatcher = new AlwaysFailingDispatcher(secondDb);
            await secondDispatcher.SweepDeadLetteredAsync(batchSize: 50, cooldown: TimeSpan.Zero, TestContext.Current.CancellationToken);

            Assert.Equal(0, secondDispatcher.DeadLetteredHookCallCount);
        }

        // Assert - both failures actually happened (Attempts climbed by 2,
        // the retry wasn't skipped), the row is still dead-lettered; only
        // the hook call count above proves it wasn't double-counted.
        using IServiceScope assertScope = factory.Services.CreateScope();
        AppBookingsDbContext assertDb = assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        OutboxMessage persisted = await assertDb.BookingsOutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Id == messageId, TestContext.Current.CancellationToken);
        Assert.Equal(11, persisted.Attempts);
        Assert.NotNull(persisted.DeadLetteredAt);
    }
}
