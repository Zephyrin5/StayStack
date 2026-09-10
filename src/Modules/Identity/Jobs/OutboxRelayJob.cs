using Identity.Outbox;
using Outbox;
using TickerQ.Utilities.Base;
namespace Identity.Jobs;

/// <summary>
///     Relays Identity's outbox rows - see Bookings.Jobs.OutboxRelayJob for
///     the shape this mirrors, and docs/adr/0003 for why.
/// </summary>
// Every value below is shared with the other modules' relay jobs (see
// OutboxRelaySchedule): only the TickerFunction names differ, because those
// must be compile-time literals and must be unique per module.
public class OutboxRelayJob(IdentityOutboxDispatcher dispatcher)
{
    [TickerFunction(functionName: "Identity.OutboxRelay", cronExpression: OutboxRelaySchedule.RelayCron)]
    public Task RelayAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.DispatchPendingAsync(OutboxRelaySchedule.BatchSize, cancellationToken);

    [TickerFunction(functionName: "Identity.OutboxDeadLetterSweep", cronExpression: OutboxRelaySchedule.DeadLetterSweepCron)]
    public Task SweepDeadLetteredAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.SweepDeadLetteredAsync(
            OutboxRelaySchedule.BatchSize, OutboxRelaySchedule.DeadLetterSweepCooldown, cancellationToken);

    [TickerFunction(functionName: "Identity.OutboxPurgeProcessed", cronExpression: OutboxRelaySchedule.PurgeProcessedCron)]
    public Task PurgeProcessedAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.PurgeProcessedAsync(
            OutboxRelaySchedule.ProcessedRetention,
            OutboxRelaySchedule.PurgeBatchSize,
            OutboxRelaySchedule.PurgeMaxBatchesPerRun,
            cancellationToken);
}
