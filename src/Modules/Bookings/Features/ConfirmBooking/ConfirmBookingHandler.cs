using Bookings.Entities.Configurations;
using Persistence;
using Bookings.Contracts;
using Bookings.Entities;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Persistence;
using BuildingBlocks.Security;
using Catalog.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Security.Cryptography;
using Promotions.Contracts;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
namespace Bookings.Features.ConfirmBooking;

public class ConfirmBookingHandler(
    AppBookingsDbContext dbContext,
    IAtomicScope atomicScope,
    IHoldConfirmation holdConfirmation,
    IPromotionRedemption promotionRedemption,
    IUnitLookup unitLookup,
    ICurrentUserProvider currentUserProvider,
    IOptions<BookingLifecyclePolicyOptions> bookingLifecycle,
    TimeProvider timeProvider) : IRequestHandler<ConfirmBookingRequest, ConfirmBookingResponse>
{
    public async ValueTask<ConfirmBookingResponse> Handle(ConfirmBookingRequest request, CancellationToken cancellationToken)
    {
        // Checked here, not in the validator: the endpoint assigns the key after
        // validation runs. Replay returns a live management token, so a counter
        // as a key would let two guests colliding on "1" reach each other's
        // booking; 16 characters admits a UUID and excludes a counter. The
        // fingerprint check in ReplayAsync is what makes a guessed key useless.
        if (request.IdempotencyKey is { Length: < 16 or > 128 })
        {
            throw new ValidationException(
                "IdempotencyKey",
                "The Idempotency-Key header must be between 16 and 128 characters. A UUID is a good choice.");
        }

        string? keyHash = request.IdempotencyKey is null ? null : SecureToken.Hash(request.IdempotencyKey);
        string requestFingerprint = ComputeRequestFingerprint(request);

        if (keyHash is not null)
        {
            CheckoutIdempotencyRecord? replayed = await dbContext.CheckoutIdempotencyRecords.AsNoTracking()
                .SingleOrDefaultAsync(r => r.KeyHash == keyHash, cancellationToken);

            if (replayed is not null)
            {
                return await ReplayAsync(replayed, requestFingerprint, cancellationToken);
            }
        }

        // Chosen once, before the scope (docs/adr/0025). A retry after a lost
        // acknowledgement recognises its own committed booking by this id, and
        // returns this same management token, whose hash that attempt stored - a
        // token minted per attempt would hand the guest a credential with no row
        // behind it. Guest checkouts only: an account already proves ownership.
        Guid bookingId = Guid.CreateVersion7();
        string? managementToken = currentUserProvider.UserId is null ? SecureToken.Generate() : null;
        Guid managementTokenId = Guid.CreateVersion7();
        Guid redemptionId = Guid.CreateVersion7();

        try
        {
            // The hold transition, the redemption, the booking, its management token
            // and the idempotency record commit together or not at all
            // (docs/design/transaction-ownership.md). Catalog participates for the
            // unit read, so it runs on this transaction's connection.
            Booking booking = await atomicScope.ExecuteAsync(
                AtomicParticipants.Bookings,
                AtomicParticipants.Bookings | AtomicParticipants.Promotions | AtomicParticipants.Catalog,
                token => ConfirmAsync(request, bookingId, managementToken, managementTokenId, redemptionId, keyHash, requestFingerprint, token),
                cancellationToken);

            return BuildResponse(booking, managementToken);
        }
        catch (Exception ex) when (keyHash is not null && ex is HoldUnavailableException or IdempotencyKeyTakenException)
        {
            // Nothing was written. Either a concurrent request with this key won the
            // hold, or it committed a different checkout under the key first. Either
            // way its record is committed now, and is what the caller gets.
            CheckoutIdempotencyRecord? byKey = await dbContext.CheckoutIdempotencyRecords.AsNoTracking()
                .SingleOrDefaultAsync(r => r.KeyHash == keyHash, cancellationToken);

            if (byKey is not null)
            {
                return await ReplayAsync(byKey, requestFingerprint, cancellationToken);
            }

            throw new NotFoundException("Hold", request.HoldId);
        }
        catch (HoldUnavailableException)
        {
            // Another confirmation took the hold, it expired, or it never existed.
            throw new NotFoundException("Hold", request.HoldId);
        }
    }

    private async Task<Booking> ConfirmAsync(
        ConfirmBookingRequest request, Guid bookingId, string? managementToken, Guid managementTokenId, Guid redemptionId,
        string? keyHash, string requestFingerprint, CancellationToken cancellationToken)
    {
        // This request's own attempt committed and lost its acknowledgement:
        // everything below committed with the booking.
        Booking? committed = await dbContext.Bookings.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

        if (committed is not null)
        {
            return committed;
        }

        // A conditional UPDATE (status = 'held' and not expired): the one winner of
        // a race for the hold. A concurrent confirmation blocks on its row lock
        // until this scope ends, then matches nothing.
        ConfirmedHold hold;
        try
        {
            hold = await holdConfirmation.ConfirmHoldAsync(request.HoldId, cancellationToken);
        }
        catch (NotFoundException)
        {
            throw new HoldUnavailableException();
        }

        Money totalPrice = hold.TotalPrice;

        if (!string.IsNullOrWhiteSpace(request.PromoCode))
        {
            // A coupon replaces the length-of-stay discount rather than stacking
            // with it, so it applies to the subtotal - read from the hold's
            // snapshot, never reconstructed from total plus discount, which does
            // not survive rounding (docs/adr/0015).
            Money couponBase = hold.Subtotal;
            PromotionRedemptionResult redemption;

            try
            {
                redemption = await promotionRedemption.RedeemAsync(
                    request.PromoCode, hold.UnitId, request.GuestEmail, couponBase,
                    bookingId, redemptionId, cancellationToken);
            }
            catch (PromotionInvalidException promotionInvalidException)
            {
                // A bare nameof() key; GlobalExceptionHandler camel-cases every
                // validation key on the way out.
                throw new ValidationException(nameof(request.PromoCode), promotionInvalidException.Message);
            }

            Money discountedPrice = couponBase - redemption.DiscountAmount;

            // Refused when the coupon does not beat the length-of-stay discount it
            // replaces (at or above the total already quoted), or when it brings the
            // total to zero or below - a zero-total booking cannot be paid, since
            // Transaction.Create refuses it. Booking.Create enforces the same
            // invariant; this gives the guest a real message. Throwing rolls back the
            // redemption and the hold transition with it.
            if (discountedPrice.Amount <= 0m || discountedPrice.Amount >= hold.TotalPrice.Amount)
            {
                throw new ValidationException(
                    nameof(request.PromoCode),
                    discountedPrice.Amount <= 0m
                        ? "This code covers the entire stay, which can't be checked out. Please book without it."
                        : "This code doesn't provide additional savings for your current stay.");
            }

            totalPrice = discountedPrice;
        }

        // The unit's current cancellation policy, snapshotted onto the booking
        // (see Booking.CancellationPolicy); the hold does not carry it.
        UnitSummary unit = await unitLookup.GetUnitAsync(hold.UnitId, cancellationToken)
                            ?? throw new NotFoundException("Unit", hold.UnitId);

        DateTimeOffset now = timeProvider.GetUtcNow();

        Booking booking = Booking.Create(
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
            unit.CancellationPolicy,
            unit.TimeZoneId,
            // The hold is off the market from here; ExpireUnpaidBookingsJob
            // cancels the booking and releases it once this passes.
            now.AddMinutes(bookingLifecycle.Value.PaymentWindowMinutes));

        dbContext.Bookings.Add(booking);

        if (managementToken is not null)
        {
            // Only the hash is stored; the plaintext is returned once, in the response.
            dbContext.BookingManagementTokens.Add(new BookingManagementToken
            {
                Id = managementTokenId,
                BookingId = bookingId,
                TokenHash = SecureToken.Hash(managementToken),
                CreatedAt = now
            });
        }

        if (keyHash is not null)
        {
            // Committed with the booking, so a record exists if and only if its
            // booking does (docs/adr/0022).
            dbContext.CheckoutIdempotencyRecords.Add(new CheckoutIdempotencyRecord
            {
                BookingId = bookingId,
                KeyHash = keyHash,
                RequestFingerprint = requestFingerprint,
                CreatedAt = now
            });
        }

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsViolationOf(CheckoutIdempotencyRecordConfiguration.KeyHashIndex))
        {
            // Two requests with one key confirming different holds: both won their
            // hold, and the other committed its record first.
            throw new IdempotencyKeyTakenException();
        }

        return booking;
    }

    // Both are answered after the scope has rolled back, from committed state.
    private sealed class HoldUnavailableException : Exception;

    private sealed class IdempotencyKeyTakenException : Exception;

    /// <summary>
    ///     Identifies the checkout a key was issued for, so a key presented
    ///     with a different payload can be refused rather than answered with
    ///     somebody else's booking.
    ///     <para>
    ///         Covers the fields that decide what gets booked and for whom. The
    ///         unit separator matters: without one, ("ab", "c") and ("a", "bc")
    ///         hash identically.
    ///     </para>
    /// </summary>
    private static string ComputeRequestFingerprint(ConfirmBookingRequest request) =>
        SecureToken.Hash(string.Join('',
            request.HoldId.ToString(),
            request.GuestName,
            request.GuestEmail,
            request.GuestPhone ?? string.Empty,
            request.PromoCode ?? string.Empty));

    /// <summary>
    ///     Answers a repeated idempotency key with the original booking.
    /// </summary>
    private async Task<ConfirmBookingResponse> ReplayAsync(
        CheckoutIdempotencyRecord record, string requestFingerprint, CancellationToken cancellationToken)
    {
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(record.RequestFingerprint), Encoding.UTF8.GetBytes(requestFingerprint)))
        {
            // Says nothing about the booking behind the key: a client bug and a
            // guessed key get the same answer.
            throw new ConflictException(
                "This Idempotency-Key was already used for a different request. Use a new key, or resend the original request unchanged.");
        }

        // The replay window, enforced on the path that hands the credential over;
        // PurgeReplayedCheckoutsJob only cleans up.
        if (timeProvider.GetUtcNow() - record.CreatedAt > TimeSpan.FromHours(bookingLifecycle.Value.CheckoutReplayWindowHours))
        {
            throw new ConflictException(
                "This Idempotency-Key has expired. Please start over.");
        }

        Booking? booking = await dbContext.Bookings.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == record.BookingId, cancellationToken);

        if (booking is null)
        {
            // Only reachable if something hard-deleted the booking, which nothing
            // does.
            throw new ConflictException(
                "The booking this Idempotency-Key refers to no longer exists. Please start over.");
        }

        // A new token, minted now. Only hashes are stored, so the original
        // plaintext cannot be returned, and a stored plaintext copy would turn one
        // read of the table into working credentials for every guest checkout in
        // the window. BookingAccessChecker matches on hash, so several valid tokens
        // per booking work, and the original stays valid (docs/adr/0022).
        //
        // None for an authenticated customer: their account proves ownership.
        string? managementToken = null;

        if (booking.CustomerId is null)
        {
            managementToken = SecureToken.Generate();

            dbContext.BookingManagementTokens.Add(new BookingManagementToken
            {
                Id = Guid.CreateVersion7(),
                BookingId = booking.Id,
                TokenHash = SecureToken.Hash(managementToken),
                CreatedAt = timeProvider.GetUtcNow()
            });

            await dbContext.SaveChangesAsync(cancellationToken);
        }

        // The booking is re-read rather than replayed verbatim: a booking
        // cancelled since the original request must not be reported as Pending.
        return BuildResponse(booking, managementToken);
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
