using Transactions.Outbox;
using Outbox;
using TickerQ.Utilities.Base;
namespace Transactions.Jobs;

/// <summary>
///     Relays MarkTransactionSucceededHandler's outbox row, including the
///     money-touching dead-letter gap docs/adr/0003 flags for
///     ConfirmBookingPaymentOutboxMessage. See Bookings.Jobs.OutboxRelayJob
///     for the shape this mirrors.
/// </summary>
// Every value below is shared with the other modules' relay jobs (see
// OutboxRelaySchedule): only the TickerFunction names differ, because those
// must be compile-time literals and must be unique per module.
public class OutboxRelayJob(TransactionsOutboxDispatcher dispatcher)
{
    [TickerFunction(functionName: "Transactions.OutboxRelay", cronExpression: OutboxRelaySchedule.RelayCron)]
    public Task RelayAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.DispatchPendingAsync(OutboxRelaySchedule.BatchSize, cancellationToken);

    [TickerFunction(functionName: "Transactions.OutboxDeadLetterSweep", cronExpression: OutboxRelaySchedule.DeadLetterSweepCron)]
    public Task SweepDeadLetteredAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.SweepDeadLetteredAsync(
            OutboxRelaySchedule.BatchSize, OutboxRelaySchedule.DeadLetterSweepCooldown, cancellationToken);

    [TickerFunction(functionName: "Transactions.OutboxPurgeProcessed", cronExpression: OutboxRelaySchedule.PurgeProcessedCron)]
    public Task PurgeProcessedAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.PurgeProcessedAsync(
            OutboxRelaySchedule.ProcessedRetention,
            OutboxRelaySchedule.PurgeBatchSize,
            OutboxRelaySchedule.PurgeMaxBatchesPerRun,
            cancellationToken);
}
