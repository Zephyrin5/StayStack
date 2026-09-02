using Transactions.Outbox;
using TickerQ.Utilities.Base;
namespace Transactions.Jobs;

/// <summary>
///     Backstop for MarkTransactionSucceededHandler's outbox row - see
///     Bookings.Jobs.OutboxRelayJob's own doc comment for the shape this
///     mirrors, and docs/adr/0003 for why.
/// </summary>
public class OutboxRelayJob(TransactionsOutboxDispatcher dispatcher)
{
    private const int BatchSize = 50;
    private static readonly TimeSpan DeadLetterSweepCooldown = TimeSpan.FromHours(1);
    // How long a processed row is kept before retention deletes it. Not zero:
    // a processed row is the only record that a compensating action was
    // dispatched at all, and these carry money, so a window of them is what
    // makes "did we actually reverse that?" answerable afterwards. 30 days
    // comfortably outlives any support conversation about a booking.
    private static readonly TimeSpan ProcessedRetention = TimeSpan.FromDays(30);

    // Larger batches than the dispatch path, because this is a plain delete
    // with no per-row side effect - but still capped per run, so a
    // long-neglected table drains over several nights instead of one
    // lock-holding sweep.
    private const int PurgeBatchSize = 1000;
    private const int PurgeMaxBatchesPerRun = 50;

    [TickerFunction(functionName: "Transactions.OutboxRelay", cronExpression: "* * * * *")]
    public Task RelayAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.DispatchPendingAsync(BatchSize, cancellationToken);

    // Closes the money-touching dead-letter gap ADR-0003 flags for
    // ConfirmBookingPaymentOutboxMessage - see Bookings.Jobs.OutboxRelayJob's
    // own SweepDeadLetteredAsync TickerFunction for why this shape.
    [TickerFunction(functionName: "Transactions.OutboxDeadLetterSweep", cronExpression: "0 * * * *")]
    public Task SweepDeadLetteredAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.SweepDeadLetteredAsync(BatchSize, DeadLetterSweepCooldown, cancellationToken);

    // Daily, off-peak. Nothing depends on these rows being gone promptly -
    // the only cost of keeping them a while longer is table size - so this
    // deliberately runs at the coarsest cadence of the three.
    [TickerFunction(functionName: "Transactions.OutboxPurgeProcessed", cronExpression: "30 3 * * *")]
    public Task PurgeProcessedAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.PurgeProcessedAsync(ProcessedRetention, PurgeBatchSize, PurgeMaxBatchesPerRun, cancellationToken);
}
