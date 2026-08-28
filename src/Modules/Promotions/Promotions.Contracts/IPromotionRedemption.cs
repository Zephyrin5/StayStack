using SeedWork.ValueObjects;
namespace Promotions.Contracts;

/// <summary>
///     Write-side contract for coupon redemption - lets Bookings redeem and
///     reverse a promo code without ever seeing Promotion/PromotionRedemption
///     or touching their tables directly. Same boundary reasoning as
///     Catalog.Contracts.IHoldConfirmation.
/// </summary>
public interface IPromotionRedemption
{
    /// <summary>
    ///     Validates and atomically redeems a code in one call: the code
    ///     must exist, not be expired, apply to this unit's host (or be
    ///     platform-wide), match currency for a fixed-amount code, still be
    ///     under its redemption cap, and not have already been used by this
    ///     guest email (or have only a reversed prior redemption - see
    ///     ReverseRedemptionAsync). Throws PromotionInvalidException (a
    ///     Promotions exception, 400) with a specific reason rather than one
    ///     generic failure, since this is a field the guest is actively
    ///     typing into. subtotal is the amount the discount applies against -
    ///     the caller decides whether that includes or excludes any
    ///     length-of-stay discount (see ConfirmBookingHandler).
    /// </summary>
    Task<PromotionRedemptionResult> RedeemAsync(
        string code,
        Guid unitId,
        string guestEmail,
        Money subtotal,
        Guid bookingId,
        CancellationToken cancellationToken);

    /// <summary>
    ///     Reverses a redemption tied to this booking: marks the
    ///     PromotionRedemption row reversed (never deletes it - the row
    ///     survives as history) and gives back both the redemption-cap slot
    ///     and the guest email for reuse. Idempotent/no-op if this booking
    ///     never redeemed anything, or already had its redemption reversed -
    ///     same shape as Catalog.Contracts.IHoldConfirmation.ReleaseHoldAsync.
    /// </summary>
    Task ReverseRedemptionAsync(Guid bookingId, CancellationToken cancellationToken);
}

public record PromotionRedemptionResult
{
    public Guid RedemptionId { get; init; }
    public Money DiscountAmount { get; init; }
}
