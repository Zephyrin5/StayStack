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
}
