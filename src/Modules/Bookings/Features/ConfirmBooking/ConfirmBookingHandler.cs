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
        // throws, nothing in Bookings has been touched yet. Cross-module
        // write, no shared transaction (docs/adr/0003), but unlike
        // BecomeHostHandler's two writes, this one has an explicit
        // compensating rollback below.
        //
        // Price/currency come from the hold's own snapshot, not a fresh
        // unit lookup - the price a customer saw when they held is the
        // price they get, even if the unit's base price changed since.
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
            // rate-adjusted subtotal (LOS discount undone), not the
            // LOS-discounted total. See PricingCalculator for why this is
            // the one PricingRule type a coupon competes with rather than
            // compounds. hold.Subtotal is read directly, not reconstructed
            // via TotalPrice + LengthOfStayDiscountAmount - that
            // reconstruction is exactly the rounding bug docs/adr/0015
            // exists to close.
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
                // A genuinely broken code - RedeemAsync itself failed, so it
                // never created a redemption to reverse. The hold is
                // already 'booked' and nothing else will ever confirm it
                // into a real Booking - releasing it means the guest has to
                // re-hold, the correct cost for a code that was never
                // valid. Enqueued via the outbox (docs/adr/0003), not a
                // direct call that could be silently lost.
                OutboxMessage releaseHoldRow = dispatcher.Enqueue(
                    new ReleaseHoldOutboxMessage(request.HoldId), BookingsJsonSerializerContext.Default.ReleaseHoldOutboxMessage);
                OutboxMessage reverseRedemptionRow = dispatcher.Enqueue(
                    new ReverseRedemptionOutboxMessage(bookingId), BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage);

                await dbContext.SaveChangesAsync(cancellationToken);
                await dispatcher.TryDispatchAsync(releaseHoldRow, cancellationToken);
                await dispatcher.TryDispatchAsync(reverseRedemptionRow, cancellationToken);

                if (redemptionException is PromotionInvalidException promotionInvalidException)
                {
                    // camelCased explicitly, not bare nameof() PascalCase -
                    // ValidationProblemDetails.Errors is a Dictionary<string,
                    // string[]>, and PropertyNamingPolicy only governs
                    // declared property names, never dictionary keys.
                    // Without this the wire key would be "PromoCode", not
                    // "promoCode" - silently breaking the client's
                    // error.errors?.promoCode lookup.
                    throw new ValidationException(
                        JsonNamingPolicy.CamelCase.ConvertName(nameof(request.PromoCode)),
                        promotionInvalidException.Message);
                }

                throw;
            }

            Money discountedPrice = couponBase - redemption.DiscountAmount;

            // The redeemed discount applies against couponBase (pre-LOS
            // subtotal) - if it's smaller than the LOS discount it just
            // replaced, that alone can land at or above hold.TotalPrice,
            // the LOS-discounted total the guest was already quoted.
            // Rejected outright rather than falling back to the LOS price:
            // RedeemAsync already consumed the code, and applying it
            // anyway for zero benefit would overcharge the guest and burn
            // their code for nothing.
            //
            // Deliberately outside the try/catch above and its hold-release -
            // unlike a genuinely broken code, this one IS valid, and the
            // guest did nothing wrong. Only the redemption gets reversed;
            // the hold stays 'booked' so a retried Confirm can still use
            // it, rather than losing the 15-minute window and, with the
            // exclusion constraint (docs/adr/0010), possibly the dates
            // themselves.
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
            // The unit's *current* cancellation policy, snapshotted now
            // rather than re-resolved at cancel time - see
            // Booking.CancellationPolicy's own doc comment. Not sourced
            // from the hold's snapshot (unlike price/currency): the hold
            // only carries what HoldAvailabilityHandler wrote via raw SQL,
            // and a cancellation policy has no bearing on the
            // exclusion-constraint machinery that record exists for - one
            // extra Catalog round trip here. Inside this try, not before
            // it: a failure here (the unit vanishing between hold and
            // confirm - narrow, but real) needs the same hold-
            // release/redemption-reversal compensation as a failed
            // Bookings.Add below, not a bare unhandled throw.
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
            // Best-effort compensation, durable via the outbox
            // (docs/adr/0003): revert the hold to 'held' and give back the
            // redeemed code (if any), so neither is left permanently
            // consumed by a Booking that was never created.
            //
            // ChangeTracker.Clear() first - the failed Booking/
            // BookingManagementToken insert above is still tracked Added,
            // and would otherwise be re-attempted by the SaveChangesAsync
            // below.
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
