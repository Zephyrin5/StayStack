using Availability.Contracts;
using Bookings.Entities;
using Bookings.Outbox;
using Bookings.Serialization;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Security;
using Catalog.Contracts;
using Mediator;
using Outbox;
using Promotions.Contracts;
using SeedWork.ValueObjects;
using System.Text.Json;
namespace Bookings.Features.ConfirmBooking;

public class ConfirmBookingHandler(
    AppBookingsDbContext dbContext,
    BookingsOutboxDispatcher dispatcher,
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

        Money totalPrice = hold.TotalPrice;
        Money? redeemedDiscountAmount = null;

        if (!string.IsNullOrWhiteSpace(request.PromoCode))
        {
            // A redeemed code is exclusive of the length-of-stay discount
            // rather than stacking with it - the coupon applies against the
            // rate-adjusted subtotal (override/multiplier already baked in,
            // LOS discount undone) rather than the LOS-discounted total. See
            // docs on StayPriceBreakdown/PricingCalculator for why this is
            // the one PricingRule type a coupon competes with rather than
            // compounds. hold.Subtotal is read directly - snapshotted on the
            // hold at creation time (see UnitAvailabilityHold.Subtotal) -
            // rather than reconstructed via hold.TotalPrice +
            // hold.LengthOfStayDiscountAmount, which is exactly the rounding
            // bug docs/adr/0015 exists to close: two independently-rounded
            // numbers added back together to recover a third.
            Money couponBase = Money.Of(hold.Subtotal, hold.TotalPrice.Currency);
            PromotionRedemptionResult? redemption = null;

            try
            {
                redemption = await promotionRedemption.RedeemAsync(
                    request.PromoCode, hold.UnitId, request.GuestEmail, couponBase,
                    bookingId, cancellationToken);
            }
            catch (Exception redemptionException)
            {
                // A genuinely broken code (doesn't exist, expired, wrong
                // currency, cap exhausted, already used by this email) -
                // RedeemAsync itself failed, so it never created a
                // redemption to reverse. The hold is already 'booked' at
                // this point and nothing else will ever confirm it into a
                // real Booking - releasing it here means the guest has to
                // re-hold before trying again, but that's the correct cost
                // for a code that was never valid in the first place.
                // Enqueued via the outbox (see docs/adr/0003) rather than a
                // direct call that could itself be silently lost.
                OutboxMessage releaseHoldRow = dispatcher.Enqueue(
                    new ReleaseHoldOutboxMessage(request.HoldId), BookingsJsonSerializerContext.Default.ReleaseHoldOutboxMessage);
                OutboxMessage reverseRedemptionRow = dispatcher.Enqueue(
                    new ReverseRedemptionOutboxMessage(bookingId), BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage);

                await dbContext.SaveChangesAsync(cancellationToken);
                await dispatcher.TryDispatchAsync(releaseHoldRow, cancellationToken);
                await dispatcher.TryDispatchAsync(reverseRedemptionRow, cancellationToken);

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

            Money discountedPrice = couponBase - redemption.DiscountAmount;

            // The redeemed discount always applies against couponBase (the
            // pre-LOS-discount subtotal, see comment above) - if it's
            // smaller than the LOS discount it just replaced, that
            // arithmetic alone can land at or above hold.TotalPrice, the
            // LOS-discounted total HoldAvailabilityHandler already quoted
            // the guest. Rejected outright rather than silently falling
            // back to the LOS price: RedeemAsync above already consumed the
            // code (redemption cap slot, guest-email reuse guard), and
            // applying it anyway for zero actual benefit would both
            // overcharge the guest relative to the quote and burn their
            // code for nothing.
            //
            // Handled here, deliberately outside the try/catch above and
            // its hold-release - unlike a genuinely broken code, this one
            // IS valid (RedeemAsync just succeeded), and the guest did
            // nothing wrong trying it. Only the redemption it just created
            // gets reversed; the hold stays 'booked' so a retried Confirm
            // (no code, or a better one) can still use it, rather than
            // making the guest lose their 15-minute window and, with the
            // exclusion constraint (docs/adr/0010), possibly the dates
            // themselves to someone else in the meantime.
            if (discountedPrice.Amount >= hold.TotalPrice.Amount)
            {
                OutboxMessage reverseRedemptionRow = dispatcher.Enqueue(
                    new ReverseRedemptionOutboxMessage(bookingId), BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage);
                await dbContext.SaveChangesAsync(cancellationToken);
                await dispatcher.TryDispatchAsync(reverseRedemptionRow, cancellationToken);

                throw new ValidationException(
                    JsonNamingPolicy.CamelCase.ConvertName(nameof(request.PromoCode)),
                    "This code doesn't provide additional savings for your current stay.");
            }

            redeemedDiscountAmount = redemption.DiscountAmount;
            totalPrice = discountedPrice;
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
                hold.Subtotal,
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
        catch (Exception)
        {
            // Best-effort compensation, now durable via the outbox instead
            // of a direct call per action (see docs/adr/0003): revert the
            // hold back to 'held', and give back the redeemed code (if any),
            // so neither is left permanently consumed by a Booking that was
            // never actually created.
            //
            // ChangeTracker.Clear() first - the failed Booking/
            // BookingManagementToken insert above is still tracked Added,
            // and would otherwise be re-attempted (and likely re-fail) by
            // the SaveChangesAsync below.
            dbContext.ChangeTracker.Clear();

            OutboxMessage releaseHoldRow = dispatcher.Enqueue(
                new ReleaseHoldOutboxMessage(request.HoldId), BookingsJsonSerializerContext.Default.ReleaseHoldOutboxMessage);
            OutboxMessage? reverseRedemptionRow = redeemedDiscountAmount is not null
                ? dispatcher.Enqueue(
                    new ReverseRedemptionOutboxMessage(bookingId), BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage)
                : null;

            await dbContext.SaveChangesAsync(cancellationToken);

            await dispatcher.TryDispatchAsync(releaseHoldRow, cancellationToken);
            if (reverseRedemptionRow is not null)
            {
                await dispatcher.TryDispatchAsync(reverseRedemptionRow, cancellationToken);
            }

            // The original failure, preserved - now that compensating is a
            // durable local write rather than two independent cross-module
            // calls that could each fail unpredictably, there's no second
            // failure mode left here worth an AggregateException for.
            throw;
        }

        return new ConfirmBookingResponse
        {
            BookingId = booking.Id,
            BookingStatus = booking.BookingStatus,
            CheckIn = booking.CheckIn,
            CheckOut = booking.CheckOut,
            TotalPrice = booking.TotalPrice.Amount,
            Currency = booking.TotalPrice.Currency,
            ManagementToken = managementToken
        };
    }
}
