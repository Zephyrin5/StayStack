using Bookings.Outbox;
using TickerQ.Utilities.Base;
namespace Bookings.Jobs;

/// <summary>
///     Backstop for CancelBookingHandler's and ConfirmBookingHandler's
///     outbox rows - the inline dispatch each handler already attempts
///     covers the common case, this covers whatever that attempt didn't
///     finish (a transient failure, or the process dying before the inline
///     attempt ran at all). See docs/adr/0003.
///     <para>
///         Runs far more often than ExpiredHoldsSweepJob/
///         ReconcileOrphanedBookingIntentsJob's 5-minute cadence, since this
///         is now the primary delivery path for these actions on a failed
///         inline attempt, not a rare-crash backstop.
///     </para>
/// </summary>
public class OutboxRelayJob(BookingsOutboxDispatcher dispatcher)
{
    private const int BatchSize = 50;

    // A dead-lettered row needs to sit for a while before its next attempt -
    // no point retrying a genuinely poisoned message every minute at the
    // same cadence as ordinary pending rows.
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

    // Every minute - the finest granularity confirmed elsewhere in this
    // codebase's cron usage (5-field, no seconds field); every other
    // TickerFunction here runs at 5-minute-or-coarser cadence.
    [TickerFunction(functionName: "Bookings.OutboxRelay", cronExpression: "* * * * *")]
    public Task RelayAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.DispatchPendingAsync(BatchSize, cancellationToken);

    // Gives dead-lettered rows another chance once an hour, using their own
    // originally-computed Payload - see OutboxDispatcherBase.
    // SweepDeadLetteredAsync's own doc comment for why this is preferred
    // over a bespoke reconciliation job.
    [TickerFunction(functionName: "Bookings.OutboxDeadLetterSweep", cronExpression: "0 * * * *")]
    public Task SweepDeadLetteredAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.SweepDeadLetteredAsync(BatchSize, DeadLetterSweepCooldown, cancellationToken);

    // Daily, off-peak. Nothing depends on these rows being gone promptly -
    // the only cost of keeping them a while longer is table size - so this
    // deliberately runs at the coarsest cadence of the three.
    [TickerFunction(functionName: "Bookings.OutboxPurgeProcessed", cronExpression: "30 3 * * *")]
    public Task PurgeProcessedAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.PurgeProcessedAsync(ProcessedRetention, PurgeBatchSize, PurgeMaxBatchesPerRun, cancellationToken);
}
