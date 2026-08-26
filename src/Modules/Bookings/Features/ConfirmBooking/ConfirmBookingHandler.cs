using Bookings.Entities;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Security;
using Catalog.Contracts;
using Mediator;
using System.Text.Json;
namespace Bookings.Features.ConfirmBooking;

public class ConfirmBookingHandler(
    AppBookingsDbContext dbContext,
    IHoldConfirmation holdConfirmation,
    IPromotionRedemption promotionRedemption,
    IUnitLookup unitLookup,
    ICurrentUserProvider currentUserProvider,
    TimeProvider timeProvider) : IRequestHandler<ConfirmBookingRequest, ConfirmBookingResponse>
{
    public async ValueTask<ConfirmBookingResponse> Handle(ConfirmBookingRequest request, CancellationToken cancellationToken)
    {
        // Confirms the hold first (marks it 'booked' in Catalog) - if this
        // throws (missing/already-consumed/expired), nothing in Bookings
        // has been touched yet. Cross-module write, no shared transaction -
        // see docs/adr/0003. Unlike BecomeHostHandler's own two writes,
        // though, this one has an explicit compensating rollback below if
        // a later step fails.
        //
        // Price/currency come from the hold's own snapshot (taken at
        // HoldAvailabilityHandler time), not a fresh unit lookup - the
        // price the customer saw when they held the range is the price
        // they get, even if the unit's base price changed since.
        ConfirmedHold hold = await holdConfirmation.ConfirmHoldAsync(request.HoldId, cancellationToken);

        // Chosen up front, not left to Booking.Create - a redeemed promo
        // code needs this booking's id to write its PromotionRedemption row
        // before the Booking itself is ever saved.
        Guid bookingId = Guid.CreateVersion7();

        decimal totalPrice = hold.TotalPrice;
        decimal? redeemedDiscountAmount = null;

        if (!string.IsNullOrWhiteSpace(request.PromoCode))
        {
            // A redeemed code is exclusive of the length-of-stay discount
            // rather than stacking with it - the coupon applies against the
            // rate-adjusted subtotal (override/multiplier already baked in,
            // LOS discount undone) rather than the LOS-discounted total. See
            // docs on StayPriceBreakdown/PricingCalculator for why this is
            // the one PricingRule type a coupon competes with rather than
            // compounds.
            decimal couponBase = hold.LengthOfStayDiscountAmount is not null
                ? hold.TotalPrice + hold.LengthOfStayDiscountAmount.Value
                : hold.TotalPrice;

            try
            {
                PromotionRedemptionResult redemption = await promotionRedemption.RedeemAsync(
                    request.PromoCode, hold.UnitId, request.GuestEmail, couponBase, hold.Currency,
                    bookingId, cancellationToken);

                redeemedDiscountAmount = redemption.DiscountAmount;
                totalPrice = couponBase - redemption.DiscountAmount;
            }
            catch (Exception redemptionException)
            {
                // The hold is already 'booked' at this point and nothing
                // else will ever confirm it into a real Booking - same
                // orphaned-inventory risk ConfirmBookingHandler's own
                // booking-save failure below compensates for, just one step
                // earlier.
                try
                {
                    await holdConfirmation.ReleaseHoldAsync(request.HoldId, cancellationToken);
                }
                catch (Exception releaseException)
                {
                    throw new AggregateException(
                        "Promo code redemption failed, and compensating hold release also failed.",
                        redemptionException, releaseException);
                }

                if (redemptionException is PromotionInvalidException promotionInvalidException)
                {
                    // camelCased explicitly, not the bare nameof() PascalCase -
                    // ValidationProblemDetails.Errors is a Dictionary<string,
                    // string[]>, and System.Text.Json's PropertyNamingPolicy
                    // (camelCase, see AppJsonSerializerContext) only governs
                    // declared property names, never dictionary keys. Without
                    // this, the wire key would be "PromoCode", not "promoCode" -
                    // silently breaking the client's error.errors?.promoCode
                    // lookup while every other property on the response stays
                    // correctly camelCased.
                    throw new ValidationException(
                        JsonNamingPolicy.CamelCase.ConvertName(nameof(request.PromoCode)),
                        promotionInvalidException.Message);
                }

                throw;
            }
        }

        // Only a guest-checkout booking gets one - an authenticated
        // caller's account is already proof of ownership, and issuing a
        // token nobody will ever use would just be a second, redundant way
        // to access the same booking. Raw value returned exactly once, in
        // the response below - only its hash is ever persisted.
        string? managementToken = currentUserProvider.UserId is null ? SecureToken.Generate() : null;

        Booking booking;

        try
        {
            // The unit's *current* cancellation policy, snapshotted onto
            // the booking now rather than re-resolved at cancel time - see
            // Booking.CancellationPolicy's own doc comment. Not sourced
            // from the hold's own snapshot (unlike price/currency): the
            // hold only ever carries what HoldAvailabilityHandler wrote
            // into unit_availability_holds via raw SQL, and a cancellation
            // policy has no bearing on the double-booking/exclusion-
            // constraint machinery that record exists for - one extra
            // Catalog round trip here, the same "extra unit lookup when
            // needed" pattern CreateStayReviewHandler already established.
            // Inside this try, not before it: a failure here (the unit
            // vanishing between hold and confirm - narrow, but real) needs
            // the exact same hold-release/redemption-reversal compensation
            // as a failed Bookings.Add below, not a bare unhandled throw.
            UnitSummary unit = await unitLookup.GetUnitAsync(hold.UnitId, cancellationToken)
                                ?? throw new NotFoundException("Unit", hold.UnitId);

            booking = Booking.Create(
                bookingId,
                hold.UnitId,
                request.HoldId,
                currentUserProvider.UserId,
                request.GuestName,
                request.GuestEmail,
                request.GuestPhone,
                hold.CheckIn,
                hold.CheckOut,
                hold.GuestCount,
                totalPrice,
                hold.Currency,
                unit.CancellationPolicy);

            dbContext.Bookings.Add(booking);

            if (managementToken is not null)
            {
                dbContext.BookingManagementTokens.Add(new BookingManagementToken
                {
                    Id = Guid.CreateVersion7(),
                    BookingId = bookingId,
                    TokenHash = SecureToken.Hash(managementToken),
                    CreatedAt = timeProvider.GetUtcNow()
                });
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception bookingSaveException)
        {
            // Best-effort compensation: revert the hold back to 'held', and
            // give back the redeemed code (if any), so neither is left
            // permanently consumed by a Booking that was never actually
            // created. Same idiom as BecomeHostHandler's rollback on its
            // second write failing, generalized to N compensating actions
            // instead of just one - a bare `throw;` after only the first
            // failure would silently lose why the booking save failed in
            // the first place, the one piece of information most needed to
            // diagnose stuck state.
            List<Exception> compensationFailures = [];

            try
            {
                await holdConfirmation.ReleaseHoldAsync(request.HoldId, cancellationToken);
            }
            catch (Exception releaseException)
            {
                compensationFailures.Add(releaseException);
            }

            if (redeemedDiscountAmount is not null)
            {
                try
                {
                    await promotionRedemption.ReverseRedemptionAsync(bookingId, cancellationToken);
                }
                catch (Exception reverseRedemptionException)
                {
                    compensationFailures.Add(reverseRedemptionException);
                }
            }

            if (compensationFailures.Count > 0)
            {
                throw new AggregateException(
                    "Booking save failed, and compensating action(s) also failed.",
                    [bookingSaveException, .. compensationFailures]);
            }

            throw;
        }

        return new ConfirmBookingResponse
        {
            BookingId = booking.Id,
            BookingStatus = booking.BookingStatus,
            CheckIn = booking.CheckIn,
            CheckOut = booking.CheckOut,
            TotalPrice = booking.TotalPrice,
            Currency = booking.Currency,
            ManagementToken = managementToken
        };
    }
}
