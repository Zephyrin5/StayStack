namespace Promotions.Contracts;

/// <summary>
///     Lets Bookings find its own orphaned-redemption reconciliation
///     candidates without ever referencing Promotions' own entities,
///     AppPromotionsDbContext, or the promotion_redemptions table directly -
///     same boundary reasoning as Availability.Contracts.IHoldLookup.
///     Deliberately read-only/separate from IPromotionRedemption: this
///     answers "which redemptions look orphaned", the actual reversal still
///     goes through IPromotionRedemption.ReverseRedemptionAsync.
/// </summary>
public interface IRedemptionLookup
{
    /// <summary>
    ///     BookingIds of still-active redemptions (ReversedAt IS NULL)
    ///     redeemed inside the window (<paramref name="earliestRedeemedAt"/>,
    ///     <paramref name="cutoff"/>] - the backstop for a redemption whose
    ///     RedeemAsync commit (a real, durable commit to Promotions' own
    ///     database, independent of anything Bookings does afterward)
    ///     succeeds before Bookings ever gets a chance to create the
    ///     Booking or enqueue a compensating ReverseRedemptionOutboxMessage.
    ///     A process crash in that narrow window leaves the redemption
    ///     permanently consumed - cap decremented, guest email marked used -
    ///     with nothing that will ever reverse it on its own; ordinary
    ///     ConfirmBookingHandler exceptions are already compensated by its
    ///     own catch blocks (see docs/adr/0003), this is only for the
    ///     process-death case those can't run for at all. Doesn't know
    ///     which of these actually lack a booking (or have one that's
    ///     Cancelled, e.g. behind a dead-lettered reversal from
    ///     CancelBookingHandler) - that's Bookings' own table to check, via
    ///     Bookings.Jobs.ReconcileOrphanedPromotionRedemptionsJob.
    ///
    ///     Bounded on both ends for the same reason as IHoldLookup's own
    ///     query: a successful, non-orphaned redemption stays ReversedAt ==
    ///     null forever (that's correct - it's still backing a real,
    ///     confirmed booking), so an upper-bound-only query would match
    ///     every redemption this app has ever made and grow without limit.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetActiveRedemptionBookingIdsOlderThanAsync(
        DateTimeOffset cutoff, DateTimeOffset earliestRedeemedAt, int maxResults, CancellationToken cancellationToken);
}
