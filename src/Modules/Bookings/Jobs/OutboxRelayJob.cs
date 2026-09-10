using Bookings.Outbox;
using Outbox;
using TickerQ.Utilities.Base;
namespace Bookings.Jobs;

/// <summary>
///     Relays CancelBookingHandler's and ConfirmBookingHandler's outbox rows.
///     The inline dispatch each handler already attempts covers the common
///     case; this covers whatever that attempt did not finish - a transient
///     failure, or the process dying before the inline attempt ran at all.
///     See docs/adr/0003.
/// </summary>
// Every value below is shared with the other modules' relay jobs (see
// OutboxRelaySchedule): only the TickerFunction names differ, because those
// must be compile-time literals and must be unique per module.
public class OutboxRelayJob(BookingsOutboxDispatcher dispatcher)
{
    [TickerFunction(functionName: "Bookings.OutboxRelay", cronExpression: OutboxRelaySchedule.RelayCron)]
    public Task RelayAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.DispatchPendingAsync(OutboxRelaySchedule.BatchSize, cancellationToken);

    [TickerFunction(functionName: "Bookings.OutboxDeadLetterSweep", cronExpression: OutboxRelaySchedule.DeadLetterSweepCron)]
    public Task SweepDeadLetteredAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.SweepDeadLetteredAsync(
            OutboxRelaySchedule.BatchSize, OutboxRelaySchedule.DeadLetterSweepCooldown, cancellationToken);

    [TickerFunction(functionName: "Bookings.OutboxPurgeProcessed", cronExpression: OutboxRelaySchedule.PurgeProcessedCron)]
    public Task PurgeProcessedAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.PurgeProcessedAsync(
            OutboxRelaySchedule.ProcessedRetention,
            OutboxRelaySchedule.PurgeBatchSize,
            OutboxRelaySchedule.PurgeMaxBatchesPerRun,
            cancellationToken);
}
