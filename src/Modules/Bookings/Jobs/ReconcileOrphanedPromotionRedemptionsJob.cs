using Bookings.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Promotions.Contracts;
using TickerQ.Utilities.Base;
namespace Bookings.Jobs;

/// <summary>
///     The redemption-side counterpart to ReconcileOrphanedBookedHoldsJob -
///     same two orphan shapes, same reasoning, applied to
///     promotion_redemptions instead of unit_availability_holds:
///     <list type="bullet">
///         <item>
///             IPromotionRedemption.RedeemAsync commits atomically to
///             Promotions' own database (redemption cap incremented, the row
///             inserted) entirely independent of anything Bookings does
///             afterward - a process crash between that commit and Bookings
///             ever getting a chance to create the Booking or enqueue a
///             compensating ReverseRedemptionOutboxMessage leaves the
///             redemption permanently consumed - cap decremented, guest
///             email marked used - against a booking that was never
///             created. Not an ordinary exception (those are already
///             compensated by ConfirmBookingHandler's own catch blocks, see
///             docs/adr/0003) - this is only for the case those can't run
///             for at all.
///         </item>
///         <item>
///             CancelBookingHandler enqueues a ReverseRedemptionOutboxMessage
///             whose retries can be exhausted (dead-lettered) before it ever
///             completes - here the booking row does exist, it's just
///             Cancelled with a redemption that's still active behind it.
///             The outbox's own hourly SweepDeadLetteredAsync already
///             retries a dead-lettered row forever, so this only matters
///             once that keeps failing - same relationship
///             ReconcileOrphanedBookedHoldsJob has to OutboxRelayJob's own
///             retry loop.
///         </item>
///     </list>
///     Deliberately owned by Bookings, not Promotions: it's Bookings that
///     knows what "orphaned" means here (a redemption whose BookingId has no
///     live Booking row). Reads Promotions' candidate booking ids through
///     IRedemptionLookup rather than joining promotion_redemptions directly
///     by table name - a raw cross-module join would be exactly the kind of
///     boundary violation docs/adr/0004 exists to prevent.
/// </summary>
public partial class ReconcileOrphanedPromotionRedemptionsJob(
    AppBookingsDbContext dbContext,
    IRedemptionLookup redemptionLookup,
    IPromotionRedemption promotionRedemption,
    TimeProvider timeProvider,
    ILogger<ReconcileOrphanedPromotionRedemptionsJob> logger)
{
    // Same margin as ReconcileOrphanedBookedHoldsJob's own Grace - generous
    // over the millisecond-scale gap between RedeemAsync's commit and the
    // Booking insert in the normal (non-crashed) path.
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(10);

    // Same reasoning as ReconcileOrphanedBookedHoldsJob's own
    // ReconciliationWindow - a successful, non-orphaned redemption stays
    // ReversedAt == null forever, so a lower bound keeps the candidate set
    // to roughly one window's worth of real orphans, not this app's entire
    // redemption history.
    private static readonly TimeSpan ReconciliationWindow = TimeSpan.FromDays(2);

    private const int MaxResultsPerRun = 1000;

    [TickerFunction(functionName: "Bookings.ReconcileOrphanedPromotionRedemptions", cronExpression: "*/5 * * * *")]
    public async Task ReconcileAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset cutoff = now - Grace;
        DateTimeOffset earliestRedeemedAt = cutoff - ReconciliationWindow;

        IReadOnlyList<Guid> staleRedemptionBookingIds = await redemptionLookup.GetActiveRedemptionBookingIdsOlderThanAsync(
            cutoff, earliestRedeemedAt, MaxResultsPerRun, cancellationToken);
        if (staleRedemptionBookingIds.Count == 0)
        {
            return;
        }

        if (staleRedemptionBookingIds.Count == MaxResultsPerRun)
        {
            LogResultsCapped(logger, MaxResultsPerRun);
        }

        // Intentionally does NOT restate the soft-delete predicate the way
        // a Tier 3 query joining an Entity-derived table normally would
        // (see docs/adr/0014) - an archived/soft-deleted booking still
        // happened, and should still protect its redemption from being
        // reversed out from under it. The soft-delete filter governs
        // visibility, not whether the booking exists.
        //
        // BookingStatus != Cancelled, not just "any booking row exists" -
        // a Cancelled booking still has a row, but its redemption is
        // exactly the second orphan case this job covers (see class doc
        // comment): a dead-lettered ReverseRedemptionOutboxMessage that's
        // been retried by the sweep without success.
        List<Guid> liveBookingIds = await dbContext.Bookings
            .Where(b => staleRedemptionBookingIds.Contains(b.Id) && b.BookingStatus != BookingStatus.Cancelled)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        IEnumerable<Guid> orphanedBookingIds = staleRedemptionBookingIds.Except(liveBookingIds);

        foreach (Guid bookingId in orphanedBookingIds)
        {
            // Idempotent and safe against a race with a legitimately-
            // completing confirm - only acts on a redemption still active
            // (ReversedAt IS NULL) for this booking.
            await promotionRedemption.ReverseRedemptionAsync(bookingId, cancellationToken);
        }
    }

    [LoggerMessage(LogLevel.Warning,
        "ReconcileOrphanedPromotionRedemptions hit its per-run cap of {MaxResultsPerRun} candidates - orphans may be arriving faster than this job clears them, and any older than the reconciliation window are no longer reachable by it at all")]
    private static partial void LogResultsCapped(ILogger logger, int maxResultsPerRun);
}
