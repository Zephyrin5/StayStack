using Availability.Contracts;
using Bookings.Entities;
using Bookings.Outbox;
using Bookings.Serialization;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Security;
using Catalog.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
        // Chosen up front: a redeemed promo code needs this id to write its
        // PromotionRedemption row before the Booking itself is ever saved, the
        // intent row below is keyed by it, and - because it's pre-generated -
        // any failed save can ask the database whether the Booking actually
        // committed rather than inferring it from an exception type.
        Guid bookingId = Guid.CreateVersion7();

        PendingBookingIntent intent = await OpenIntentAsync(request.HoldId, bookingId, cancellationToken);

        // ExecuteDelete, not a tracked Remove: on a failure path a zero-row
        // delete just means the reconcile job got here first, which has to be
        // a clean no-op. A tracked delete asserts affected rows, so batched
        // with the compensating enqueues below it would throw, roll those rows
        // back so they're never written, and replace the real exception with
        // an EF concurrency error. Detaches afterward - ExecuteDelete bypasses
        // the change tracker, leaving the instance Unchanged against a row
        // that no longer exists.
        async Task DiscardIntentAsync()
        {
            await dbContext.PendingBookingIntents
                .Where(i => i.Id == bookingId)
                .ExecuteDeleteAsync(cancellationToken);
            dbContext.Entry(intent).State = EntityState.Detached;
        }

        ConfirmedHold hold;

        try
        {
            // Confirms the hold first (marks it 'booked' in Availability).
            // Cross-module write, no shared transaction (docs/adr/0003) - but
            // no longer unmarked: the intent row above is already durable, so
            // even a hard process death on the next line leaves something for
            // ReconcileOrphanedBookingIntentsJob to recover from.
            //
            // Price/currency come from the hold's own snapshot, not a fresh
            // unit lookup - the price a customer saw when they held is the
            // price they get, even if the unit's base price changed since.
            hold = await holdConfirmation.ConfirmHoldAsync(request.HoldId, cancellationToken);
        }
        catch (Exception)
        {
            // An ordinary failure, not a crash - most often this hold was
            // already consumed by an earlier attempt. Without this the intent
            // would sit until the grace period elapsed and the job released a
            // hold that nothing was wrong with.
            await DiscardIntentAsync();
            throw;
        }

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

                // After the compensating save, deliberately - see
                // DiscardIntentAsync's own comment. A crash between the two
                // leaves the intent alive and the job repeats these
                // (idempotent) compensations, which is safe.
                await DiscardIntentAsync();

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

                // The hold deliberately stays 'booked' (see above), so the
                // intent has to go - otherwise the job would release it.
                await DiscardIntentAsync();

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

            // A *tracked* delete, unlike every failure path above, and this
            // is the whole correctness argument (docs/adr/0017). EF asserts
            // affected rows on it, so if the reconcile job already resolved
            // this intent the save throws - and because the delete and the
            // Booking insert share one transaction, no Booking is written.
            // Timing can't provide this: nothing here re-validates the hold,
            // so without it a job firing mid-request would release a live
            // hold and reverse a live redemption underneath a booking that
            // then commits anyway.
            dbContext.PendingBookingIntents.Remove(intent);

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // ChangeTracker.Clear() first - the failed Booking/
            // BookingManagementToken insert above is still tracked Added,
            // and would otherwise be re-attempted by the SaveChangesAsync
            // below. Clearing also removes any identity-map ambiguity from
            // the lookup that follows.
            dbContext.ChangeTracker.Clear();

            // Ask the database what actually happened; never infer it from
            // the exception type. SaveChangesAsync runs under
            // EnableRetryOnFailure, and an execution strategy cannot tell a
            // failed transaction from one that committed and lost its
            // acknowledgement - it just re-runs the batch, which then fails
            // on its own already-committed rows. Which exception that
            // surfaces depends on EF's internal command ordering (a
            // duplicate-key DbUpdateException if the insert replays first, a
            // zero-row DbUpdateConcurrencyException if the delete does), so
            // the verdict has to come from the row, not the type. Without
            // this, a committed booking gets "compensated": its hold
            // released back to immediately-re-bookable, its redemption
            // reversed, and a 500 returned for a booking that succeeded.
            Booking? committed = await dbContext.Bookings.AsNoTracking()
                .SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

            if (committed is not null)
            {
                // The committed batch necessarily included this intent's own
                // delete (they share one transaction), so this is belt and
                // braces - but the cost of being wrong is the worst outcome
                // in the system: a surviving intent behind a live booking
                // gets reconciled later, releasing that booking's hold back
                // to immediately-re-bookable. Cheap to make certain rather
                // than reason about.
                await DiscardIntentAsync();
                return BuildResponse(committed, managementToken);
            }

            if (ex is DbUpdateConcurrencyException)
            {
                // The intent was gone and no Booking exists: the reconcile
                // job resolved this while the request was in flight. It has
                // already released the hold and reversed any redemption, so
                // compensating again is skipped deliberately - this reads
                // like a missing compensation otherwise.
                throw new ConflictException(
                    "This booking confirmation timed out and was rolled back. Please start over.");
            }

            // Best-effort compensation, durable via the outbox
            // (docs/adr/0003): revert the hold to 'held' and give back the
            // redeemed code (if any), so neither is left permanently
            // consumed by a Booking that was never created.
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

            await DiscardIntentAsync();

            // The original failure, preserved - now that compensating is a
            // durable local write rather than two independent cross-module
            // calls that could each fail unpredictably, there's no second
            // failure mode left here worth an AggregateException for.
            throw;
        }

        return BuildResponse(booking, managementToken);
    }

    /// <summary>
    ///     Writes the durable marker that this confirmation has begun, before
    ///     any cross-module work happens. Returns the tracked instance the
    ///     success path later removes.
    /// </summary>
    private async Task<PendingBookingIntent> OpenIntentAsync(Guid holdId, Guid bookingId, CancellationToken cancellationToken)
    {
        PendingBookingIntent intent = new PendingBookingIntent
        {
            Id = bookingId,
            HoldId = holdId,
            CreatedAt = timeProvider.GetUtcNow()
        };

        dbContext.PendingBookingIntents.Add(intent);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return intent;
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Detached before anything else so the Attach below can't collide
            // with this instance in the identity map, and so the throwing
            // branches don't leave a phantom Added row behind.
            dbContext.Entry(intent).State = EntityState.Detached;

            PendingBookingIntent? existing = await dbContext.PendingBookingIntents.AsNoTracking()
                .SingleOrDefaultAsync(i => i.HoldId == holdId, cancellationToken);

            if (existing is null)
            {
                // The conflicting intent was resolved between the violation
                // and this read, so a retry would now succeed.
                throw new ConflictException(
                    "A previous confirmation for this hold was interrupted and is being cleaned up. Please try again shortly.");
            }

            if (existing.Id != bookingId)
            {
                // A different request owns this hold. Deliberately refuses
                // rather than taking the intent over: taking over would
                // replay a redemption that may already hold the
                // (promotion_id, guest_email) slot.
                throw new ConflictException(
                    existing.CreatedAt > timeProvider.GetUtcNow() - PendingBookingIntent.ReconcileGrace
                        ? "A confirmation for this hold is already in progress."
                        : "A previous confirmation for this hold was interrupted and is being cleaned up. Please try again shortly.");
            }

            // Our own insert: it committed and the acknowledgement was lost
            // to an execution-strategy retry (see EnableRetryOnFailure).
            //
            // Re-attaching is not cosmetic. The instance above is still
            // tracked Added, and Remove on an Added entity detaches it rather
            // than marking it Deleted - EF would emit no DELETE at all, which
            // silently disables the success path's row-count assertion (the
            // structural guarantee) *and* leaves this row alive behind a
            // confirmed booking for the job to reconcile later, releasing the
            // hold underneath it.
            dbContext.Attach(existing);
            return existing;
        }
    }

    private static ConfirmBookingResponse BuildResponse(Booking booking, string? managementToken) =>
        new ConfirmBookingResponse
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
