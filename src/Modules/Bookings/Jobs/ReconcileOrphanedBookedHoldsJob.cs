using Availability.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Base;
namespace Bookings.Jobs;

/// <summary>
///     The backstop docs/adr/0003 anticipated but the original sweep jobs
///     never targeted: ConfirmBookingHandler's first write
///     (IHoldConfirmation.ConfirmHoldAsync) marks a hold 'booked' before the
///     Booking row that's supposed to follow it even exists. A process death
///     in that narrow window - not an ordinary exception, those are already
///     compensated by ConfirmBookingHandler's own catch blocks - leaves the
///     hold stuck 'booked' forever; ExpiredHoldsSweepJob only ever looks at
///     'held' rows, so nothing else would ever find it.
///
///     Deliberately owned by Bookings, not Availability: it's Bookings that
///     knows what "orphaned" means here (a hold with no matching
///     bookings.hold_id row). Reads Availability's candidate hold ids
///     through IHoldLookup rather than joining unit_availability_holds
///     directly by table name - a raw cross-module join would be exactly
///     the kind of boundary violation docs/adr/0004 exists to prevent, even
///     though this job happens to live on the "allowed to call Availability"
///     side of that boundary.
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

    // A successfully-booked hold stays 'booked' forever (it's the confirmed
    // booking's own permanent double-booking guard) - without a lower bound
    // too, this job's candidate query would match every booking ever made
    // and grow without limit as the app ages. Two days is generous margin
    // over the 5-minute run cadence - wide enough to still catch a crash
    // orphan even if the scheduler itself was down for a while, without
    // scanning this app's entire history every run.
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

        // The window's own tradeoff (see ReconciliationWindow above) means an
        // orphan older than earliestBookedAt is silently unreachable to this
        // job forever, not just delayed - hitting the cap is the only signal
        // that orphans are arriving faster than this job clears them, which
        // is exactly when that tradeoff starts costing real orphans instead
        // of just being a safe margin.
        if (staleBookedHoldIds.Count == MaxResultsPerRun)
        {
            LogResultsCapped(logger, MaxResultsPerRun);
        }

        // Intentionally does NOT restate the soft-delete predicate the way
        // a Tier 3 query joining an Entity-derived table normally would
        // (see docs/adr/0014) - an archived/soft-deleted booking still
        // happened, and should still protect its hold from being released
        // out from under it. The soft-delete filter governs visibility, not
        // whether the booking exists.
        List<Guid> holdIdsWithBookings = await dbContext.Bookings
            .Where(b => staleBookedHoldIds.Contains(b.HoldId))
            .Select(b => b.HoldId)
            .ToListAsync(cancellationToken);

        IEnumerable<Guid> orphanedHoldIds = staleBookedHoldIds.Except(holdIdsWithBookings);

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
