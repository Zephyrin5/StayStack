using Bookings;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Outbox;
namespace IntegrationTests.Features.Bookings;

// Outbox tables only ever grew: every confirm and every cancel writes rows
// that are dead weight the moment they dispatch, and nothing deleted them.
//
// What the purge must NOT delete matters more than what it does. An
// unprocessed row is still owed its side effect, and a dead-lettered row that
// never processed is one a human still has to look at - deleting either turns
// "this needs attention" into "this never happened", which is strictly worse
// than an oversized table.
[Collection("Integration Tests")]
public class OutboxPurgeTests(IntegrationTestWebApplicationFactory factory)
{
    private sealed class PurgeTestDispatcher(
        AppBookingsDbContext dbContext, TimeProvider timeProvider, ILogger<PurgeTestDispatcher> logger)
        : OutboxDispatcherBase<AppBookingsDbContext>(dbContext, timeProvider, logger)
    {
        protected override string ModuleName => "PurgeTest";

        protected override Task TryHandleAsync(OutboxMessage message, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }

    private static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private static OutboxMessage Row(
        string type, DateTimeOffset? processedAt, DateTimeOffset? deadLetteredAt = null) =>
        new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = type,
            Payload = "{}",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-90),
            NextAttemptAt = DateTimeOffset.UtcNow.AddDays(-90),
            ProcessedAt = processedAt,
            DeadLetteredAt = deadLetteredAt
        };

    [Fact]
    public async Task PurgeProcessedAsync_DeletesOnlyProcessedRowsPastRetention()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;

        OutboxMessage oldProcessed = Row("PurgeOldProcessed", processedAt: now - Retention.Add(TimeSpan.FromDays(1)));
        OutboxMessage recentProcessed = Row("PurgeRecentProcessed", processedAt: now.AddDays(-1));
        OutboxMessage neverProcessed = Row("PurgeNeverProcessed", processedAt: null);
        OutboxMessage deadLettered = Row("PurgeDeadLettered", processedAt: null, deadLetteredAt: now.AddDays(-60));
        OutboxMessage deadLetteredThenResolved = Row(
            "PurgeDeadLetteredResolved",
            processedAt: now - Retention.Add(TimeSpan.FromDays(1)),
            deadLetteredAt: now.AddDays(-60));

        using (IServiceScope seedScope = factory.Services.CreateScope())
        {
            AppBookingsDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            seedDb.BookingsOutboxMessages.AddRange(
                oldProcessed, recentProcessed, neverProcessed, deadLettered, deadLetteredThenResolved);
            await seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        PurgeTestDispatcher dispatcher = new PurgeTestDispatcher(
            dbContext, TimeProvider.System, NullLogger<PurgeTestDispatcher>.Instance);

        await dispatcher.PurgeProcessedAsync(
            Retention, batchSize: 1000, maxBatchesPerRun: 50, TestContext.Current.CancellationToken);

        // Gone: processed and past retention.
        Assert.False(await ExistsAsync(oldProcessed.Id));

        // A dead letter that was later resolved carries a ProcessedAt, so it
        // is ordinary history and purges like any other.
        Assert.False(await ExistsAsync(deadLetteredThenResolved.Id));

        // Kept: processed, but still inside the retention window. This is the
        // record that a compensating action was dispatched at all.
        Assert.True(await ExistsAsync(recentProcessed.Id));

        // Kept: still owed its side effect, however old. Age is not evidence
        // that a message no longer matters - a row stuck for 90 days is the
        // one most likely to matter.
        Assert.True(await ExistsAsync(neverProcessed.Id));

        // Kept: dead-lettered and never processed. The sweep still retries
        // this hourly, and it is what a human would go looking for.
        Assert.True(await ExistsAsync(deadLettered.Id));
    }

    [Fact]
    public async Task PurgeProcessedAsync_StopsAtTheBatchCap_SoOneRunCannotDeleteUnbounded()
    {
        DateTimeOffset processedAt = DateTimeOffset.UtcNow - Retention.Add(TimeSpan.FromDays(1));
        List<OutboxMessage> rows = [.. Enumerable.Range(0, 6).Select(_ => Row("PurgeCapped", processedAt))];

        using (IServiceScope seedScope = factory.Services.CreateScope())
        {
            AppBookingsDbContext seedDb = seedScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            seedDb.BookingsOutboxMessages.AddRange(rows);
            await seedDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        PurgeTestDispatcher dispatcher = new PurgeTestDispatcher(
            dbContext, TimeProvider.System, NullLogger<PurgeTestDispatcher>.Instance);

        // Two batches of two: the cap, not the amount of eligible data, is
        // what bounds the run. Draining the rest is the next run's job.
        int purged = await dispatcher.PurgeProcessedAsync(
            Retention, batchSize: 2, maxBatchesPerRun: 2, TestContext.Current.CancellationToken);

        Assert.Equal(4, purged);

        int remaining = 0;
        foreach (OutboxMessage row in rows)
        {
            if (await ExistsAsync(row.Id))
            {
                remaining++;
            }
        }

        Assert.Equal(2, remaining);
    }

    private async Task<bool> ExistsAsync(Guid id)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        return await dbContext.BookingsOutboxMessages
            .AsNoTracking()
            .AnyAsync(m => m.Id == id, TestContext.Current.CancellationToken);
    }
}
