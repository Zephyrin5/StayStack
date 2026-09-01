using Availability.Contracts;
using Bookings.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Base;
namespace Bookings.Jobs;

/// <summary>
///     SUPERSEDED by ReconcileOrphanedBookingIntentsJob (docs/adr/0017) -
///     <b>delete this, IHoldLookup, and its DI registration after one
///     release.</b> Kept running for exactly one release because an orphan
///     created before the intent table existed has no intent row, so the new
///     job cannot see it; this one still can. Note the overlap only drains
///     what this job could ever reach - an orphan older than
///     ReconciliationWindow was already unreachable before ADR-0017 and stays
///     that way.
///
///     A backstop for two distinct ways a hold can end up stuck 'booked'
///     with nothing that will ever release it:
///     <list type="bullet">
///         <item>
///             ConfirmBookingHandler's first write (ConfirmHoldAsync) marks
///             a hold 'booked' before the Booking row that's supposed to
///             follow it exists. A process death in that narrow window -
///             not an ordinary exception, those are already compensated by
///             ConfirmBookingHandler's own catch blocks - leaves the hold
///             with no matching bookings.hold_id row.
///         </item>
///         <item>
///             CancelBookingHandler enqueues a ReleaseHoldOutboxMessage
///             (docs/adr/0003) whose retries can be exhausted before it
///             completes - here the booking row exists, it's just
///             Cancelled with a hold still 'booked' behind it.
///             OutboxRelayJob already covers the common retry case; this
///             only matters once that's exhausted.
///         </item>
///     </list>
///     Either way, ExpiredHoldsSweepJob only ever looks at 'held' rows, so
///     nothing else would ever find these.
///
///     Owned by Bookings, not Availability - Bookings is what knows what
///     "orphaned" means here. Reads candidate hold ids through IHoldLookup
///     rather than joining unit_availability_holds directly - a raw
///     cross-module join would be exactly the boundary violation
///     docs/adr/0004 exists to prevent, even on the "allowed to call
///     Availability" side.
/// </summary>
public partial class ReconcileOrphanedBookedHoldsJob(
    AppBookingsDbContext dbContext,
    IHoldLookup holdLookup,
    IHoldConfirmation holdConfirmation,
    TimeProvider timeProvider,
    ILogger<ReconcileOrphanedBookedHoldsJob> logger)
{
    // Generous margin over the millisecond-scale gap between ConfirmHoldAsync
    // and the Booking insert in the normal (non-crashed) path - wide enough
    // that this never races a request that's still legitimately in flight.
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    // A successfully-booked hold stays 'booked' forever - without a lower
    // bound too, the candidate query would match every booking ever made
    // and grow unbounded. Two days is generous margin over the 5-minute
    // cadence - wide enough to catch a crash orphan even after scheduler
    // downtime, without scanning the app's entire history every run.
    private static readonly TimeSpan ReconciliationWindow = TimeSpan.FromDays(2);

    // A hard ceiling independent of the window above, in case an unexpected
    // volume of orphans ever appears in one run - caps the work this job
    // does in a single invocation rather than assuming the window alone
    // keeps the result set small.
    private const int MaxResultsPerRun = 1000;

    [TickerFunction(functionName: "Bookings.ReconcileOrphanedBookedHolds", cronExpression: "*/5 * * * *")]
    public async Task ReconcileAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset cutoff = now - Grace;
        DateTimeOffset earliestBookedAt = cutoff - ReconciliationWindow;

        IReadOnlyList<Guid> staleBookedHoldIds = await holdLookup.GetBookedHoldIdsOlderThanAsync(
            cutoff, earliestBookedAt, MaxResultsPerRun, cancellationToken);
        if (staleBookedHoldIds.Count == 0)
        {
            return;
        }

        // The window's tradeoff (ReconciliationWindow above) means an
        // orphan older than earliestBookedAt is unreachable forever, not
        // just delayed - hitting the cap is the only signal orphans are
        // arriving faster than this job clears them, exactly when that
        // tradeoff starts costing real orphans.
        if (staleBookedHoldIds.Count == MaxResultsPerRun)
        {
            LogResultsCapped(logger, MaxResultsPerRun);
        }

        // Intentionally does NOT restate the soft-delete predicate the way
        // a Tier 3 query normally would (docs/adr/0014) - an archived
        // booking still happened, and should still protect its hold from
        // release. The soft-delete filter governs visibility, not whether
        // the booking exists.
        //
        // BookingStatus != Cancelled, not just "any booking row exists" -
        // a Cancelled booking still has a row, but its hold is exactly the
        // second orphan case this job now covers (see class doc comment):
        // a dead-lettered ReleaseHoldOutboxMessage that will never retry
        // again on its own.
        List<Guid> holdIdsWithLiveBookings = await dbContext.Bookings
            .Where(b => staleBookedHoldIds.Contains(b.HoldId) && b.BookingStatus != BookingStatus.Cancelled)
            .Select(b => b.HoldId)
            .ToListAsync(cancellationToken);

        IEnumerable<Guid> orphanedHoldIds = staleBookedHoldIds.Except(holdIdsWithLiveBookings);

        foreach (Guid holdId in orphanedHoldIds)
        {
            // Idempotent and safe against a race with a legitimately-
            // completing confirm - only acts on a row still 'booked'.
            await holdConfirmation.ReleaseHoldAsync(holdId, cancellationToken);
        }
    }

    [LoggerMessage(LogLevel.Warning,
        "ReconcileOrphanedBookedHolds hit its per-run cap of {MaxResultsPerRun} candidates - orphans may be arriving faster than this job clears them, and any older than the reconciliation window are no longer reachable by it at all")]
    private static partial void LogResultsCapped(ILogger logger, int maxResultsPerRun);
}
