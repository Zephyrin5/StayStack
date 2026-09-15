using Bookings.Entities.Configurations;
using Persistence;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Outbox;
using Bookings.Serialization;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Security;
using Catalog.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using System.Text;
using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Outbox;
using Promotions.Contracts;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
namespace Bookings.Features.ConfirmBooking;

public class ConfirmBookingHandler(
    AppBookingsDbContext dbContext,
    BookingsOutboxDispatcher dispatcher,
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

        Guid bookingId = Guid.CreateVersion7();

        // Transaction A: the hold transition and the intent commit together, so
        // the intent is present if and only if the hold moved (docs/adr/0017).
        ConfirmationStart start = await BeginConfirmationAsync(
            request.HoldId, bookingId, keyHash, requestFingerprint, cancellationToken);

        // A concurrent request with the same key finished between the read above
        // and this request's reservation. Replayed here, after the transaction is
        // resolved, because ReplayAsync mints a management token and its hash
        // must commit before the guest receives it (docs/adr/0025).
        if (start.ReplayRecord is not null)
        {
            // Nothing from the abandoned attempt may ride along with the token
            // insert. Only this branch clears; the success path needs its intent
            // and record tracked.
            dbContext.ChangeTracker.Clear();

            return await ReplayAsync(start.ReplayRecord, requestFingerprint, cancellationToken);
        }

        // Set whenever ReplayRecord is null.
        PendingBookingIntent intent = start.Intent!;
        ConfirmedHold hold = start.Hold!;
        CheckoutIdempotencyRecord? idempotencyRecord = start.Record;

        // ExecuteDelete, not a tracked Remove: on a failure path a zero-row delete
        // means the reconcile job got there first, and must be a no-op. A tracked
        // delete asserts one affected row, so it would throw inside the
        // compensating save and roll back the outbox rows saved with it. The
        // instance is detached afterwards, since ExecuteDelete bypasses the
        // tracker.
        async Task DiscardIntentAsync()
        {
            await dbContext.PendingBookingIntents
                .Where(i => i.Id == bookingId)
                .ExecuteDeleteAsync(cancellationToken);
            dbContext.Entry(intent).State = EntityState.Detached;

            // Frees the key for the client's retry. Filtered on CompletedAt ==
            // null because this also runs on the path where the booking did
            // commit, and there the completed record is the guest's replay.
            await dbContext.CheckoutIdempotencyRecords
                .Where(r => r.BookingId == bookingId && r.CompletedAt == null)
                .ExecuteDeleteAsync(cancellationToken);

            if (idempotencyRecord is not null)
            {
                dbContext.Entry(idempotencyRecord).State = EntityState.Detached;
            }
        }

        // The compensation every failure path below runs. The order is the
        // content:
        //
        //  - Enqueue then save, so the compensations are durable rows before
        //    anything acts on them (docs/adr/0003); dispatch is best effort and
        //    the relay delivers the rest.
        //  - Save before discarding the intent. The intent is the marker the
        //    reconcile job recovers from; discarded first, a crash would leave
        //    neither it nor the outbox rows. A crash after the save leaves the
        //    intent, and the job repeats compensations whose handlers verify
        //    state.
        async Task CompensateAsync(bool reverseRedemption)
        {
            OutboxMessage releaseHoldRow = dispatcher.Enqueue(
                new ReleaseHoldOutboxMessage(request.HoldId), BookingsJsonSerializerContext.Default.ReleaseHoldOutboxMessage);
            OutboxMessage? reverseRedemptionRow = reverseRedemption
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
        }

        Money totalPrice = hold.TotalPrice;
        Money? redeemedDiscountAmount = null;

        if (!string.IsNullOrWhiteSpace(request.PromoCode))
        {
            // A coupon replaces the length-of-stay discount rather than stacking
            // with it, so it applies to the subtotal - read from the hold's
            // snapshot, never reconstructed from total plus discount, which does
            // not survive rounding (docs/adr/0015).
            Money couponBase = hold.Subtotal;
            PromotionRedemptionResult? redemption = null;

            try
            {
                redemption = await promotionRedemption.RedeemAsync(
                    request.PromoCode, hold.UnitId, request.GuestEmail, couponBase,
                    bookingId, cancellationToken);
            }
            catch (Exception redemptionException)
            {
                // Reversed even though RedeemAsync threw: the reversal is a no-op
                // when nothing was redeemed, and guessing wrong the other way would
                // burn a single-use code. The hold is released too - it is
                // 'pending_payment', and a retried confirmation cannot claim it.
                await CompensateAsync(reverseRedemption: true);

                if (redemptionException is PromotionInvalidException promotionInvalidException)
                {
                    // A bare nameof() key; GlobalExceptionHandler camel-cases
                    // every validation key on the way out.
                    throw new ValidationException(
                        nameof(request.PromoCode),
                        promotionInvalidException.Message);
                }

                throw;
            }

            Money discountedPrice = couponBase - redemption.DiscountAmount;

            // Refused when the coupon does not beat the length-of-stay discount
            // it replaces (at or above the total already quoted), or when it
            // brings the total to zero or below - a zero-total booking cannot be
            // paid, since Transaction.Create refuses it. Booking.Create enforces
            // the same invariant; this gives the guest a real message.
            //
            // Both the redemption and the hold are compensated. Leaving the hold
            // 'pending_payment' for a retry would strand it: ConfirmHoldAsync
            // accepts only 'held', and once the intent is discarded nothing
            // points at the hold. Releasing costs the guest their hold window
            // (hold_expires_at is reset to now), and the expired row does not
            // block a re-hold, which deletes expired 'held' rows first.
            if (discountedPrice.Amount <= 0m || discountedPrice.Amount >= hold.TotalPrice.Amount)
            {
                // The redemption succeeded, so it needs reversing.
                await CompensateAsync(reverseRedemption: true);

                throw new ValidationException(
                    nameof(request.PromoCode),
                    discountedPrice.Amount <= 0m
                        ? "This code covers the entire stay, which can't be checked out. Please book without it."
                        : "This code doesn't provide additional savings for your current stay.");
            }

            redeemedDiscountAmount = redemption.DiscountAmount;
            totalPrice = discountedPrice;
        }

        // Guest checkouts only: an account already proves ownership. The plaintext
        // is returned once, in the response; only its hash is stored.
        string? managementToken = currentUserProvider.UserId is null ? SecureToken.Generate() : null;

        Booking booking;

        try
        {
            // The unit's current cancellation policy, snapshotted onto the booking
            // (see Booking.CancellationPolicy); the hold does not carry it. Inside
            // the try, so a unit vanishing between hold and confirm is compensated
            // like any other failure here.
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
                unit.CancellationPolicy,
                unit.TimeZoneId,
                // The hold is off the market from here; ExpireUnpaidBookingsJob
                // cancels the booking and releases it once this passes.
                timeProvider.GetUtcNow().AddMinutes(bookingLifecycle.Value.PaymentWindowMinutes));

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

            // A tracked delete, unlike the failure paths, and that is what
            // excludes the reconcile job (docs/adr/0017): EF asserts one affected
            // row, so if the job already removed the intent the save throws and,
            // sharing a transaction with the insert, writes no Booking.
            dbContext.PendingBookingIntents.Remove(intent);

            // Completed in the same save as the Booking, so the record says there is
            // a booking to replay if and only if there is one.
            if (idempotencyRecord is not null)
            {
                // CompletedAt only. The token is never stored in plaintext; replay
                // mints a new one (docs/adr/0022).
                idempotencyRecord.CompletedAt = timeProvider.GetUtcNow();
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // The failed inserts are still tracked Added; cleared so the
            // compensating save below does not retry them.
            dbContext.ChangeTracker.Clear();

            // Ask the database; never infer from the exception type. The save runs
            // under the execution strategy, which re-runs a batch whose commit
            // landed and lost its acknowledgement; which exception that surfaces
            // depends on EF's command ordering. Compensating a booking that did
            // commit would release its hold and reverse its redemption, then report
            // a failure for a booking that exists.
            Booking? committed = await dbContext.Bookings.AsNoTracking()
                .SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

            if (committed is not null)
            {
                // The committed batch deleted the intent, so this is a guard, not a
                // correction: a surviving intent behind a live booking would be
                // reconciled later and release that booking's hold.
                await DiscardIntentAsync();
                return BuildResponse(committed, managementToken);
            }

            if (ex is DbUpdateConcurrencyException)
            {
                // No intent and no booking: the reconcile job resolved this while the
                // request was in flight. Both halves are compensated here; the job's
                // own redemption reversal is not enough, because it can be dispatched
                // before RedeemAsync commits, find nothing, and be marked processed
                // (docs/adr/0017).
                //
                // Releasing the hold again is safe because of this row, not because
                // compensation is idempotent:
                //
                //   - ReleaseHoldAsync matches status IN ('pending_payment',
                //     'booked'); the job already set the row to 'held'.
                //   - The job set hold_expires_at = now, and ConfirmHoldAsync
                //     requires hold_expires_at > now, so no confirmation can claim
                //     the row again.
                //   - Hold ids are never reused.
                //
                // Widening ReleaseHoldAsync to include 'held', or reusing hold ids,
                // would make this release someone else's inventory.
                await CompensateAsync(reverseRedemption: redeemedDiscountAmount is not null);

                throw new ConflictException(
                    "This booking confirmation timed out and was rolled back. Please start over.");
            }

            // Any other failure: release the hold, and reverse the redemption if a
            // code was redeemed.
            await CompensateAsync(reverseRedemption: redeemedDiscountAmount is not null);

            // The original failure, rethrown; the compensation is a durable local
            // write with no second failure mode worth aggregating.
            throw;
        }

        return BuildResponse(booking, managementToken);
    }

    /// <summary>
    ///     Either a confirmation that has begun - <see cref="Intent"/> and
    ///     <see cref="Hold"/> set - or the record a replay should be built from.
    ///     Exactly one arm is populated.
    ///     <para>
    ///         <see cref="ReplayRecord"/> is the record, not a response: building a
    ///         response mints a management token and saves its hash, which must
    ///         happen after this transaction is resolved, outside the execution
    ///         strategy. Returning the decision makes that boundary structural
    ///         rather than something each branch has to remember.
    ///     </para>
    /// </summary>
    private sealed record ConfirmationStart
    {
        public CheckoutIdempotencyRecord? ReplayRecord { get; init; }
        public PendingBookingIntent? Intent { get; init; }
        public ConfirmedHold? Hold { get; init; }
        public CheckoutIdempotencyRecord? Record { get; init; }
    }

    private async Task<ConfirmationStart> BeginConfirmationAsync(
        Guid holdId, Guid bookingId, string? keyHash, string requestFingerprint,
        CancellationToken cancellationToken)
    {
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            // A retry after a committed-but-unacknowledged save would otherwise Add
            // a second instance under a key already tracked, failing on the
            // identity map rather than on anything real.
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // The hold transition first: it decides every race for the hold.
            // ConfirmHoldAsync is a conditional UPDATE (status = 'held' and not
            // expired), an exactly-one-winner compare-and-set; a concurrent
            // transaction blocks on the winner's row lock, re-evaluates against the
            // committed row, matches nothing, and never reaches the intent insert.
            // It joins this transaction (HoldConfirmation.AmbientTransaction).
            ConfirmedHold confirmed;

            try
            {
                confirmed = await holdConfirmation.ConfirmHoldAsync(holdId, cancellationToken);
            }
            catch (NotFoundException)
            {
                // No row matched: another confirmation took the hold, it expired,
                // it never existed - or an earlier attempt of this request
                // committed and lost its acknowledgement. Only this request's own
                // pre-generated booking id can separate the last case. Nothing is
                // staged in this transaction yet, so there is nothing to unpick.
                return await RecoverOwnCommittedAttemptAsync(
                    holdId, bookingId, keyHash, transaction, cancellationToken);
            }

            PendingBookingIntent intent = new PendingBookingIntent
            {
                Id = bookingId,
                HoldId = holdId,
                CreatedAt = timeProvider.GetUtcNow()
            };

            dbContext.PendingBookingIntents.Add(intent);

            // Reserved with the hold transition, so a rollback frees the key and
            // the record exists if and only if the confirmation began
            // (docs/adr/0022).
            CheckoutIdempotencyRecord? record = keyHash is null
                ? null
                : new CheckoutIdempotencyRecord
                {
                    BookingId = bookingId,
                    KeyHash = keyHash,
                    RequestFingerprint = requestFingerprint,
                    CreatedAt = timeProvider.GetUtcNow()
                };

            if (record is not null)
            {
                dbContext.CheckoutIdempotencyRecords.Add(record);
            }

            try
            {
                await dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException ex)
                when (ex.IsViolationOfAny(PendingBookingIntentConfiguration.HoldIndex, CheckoutIdempotencyRecordConfiguration.KeyHashIndex))
            {
                // Neither violation is a race for the hold; the UPDATE settled that.
                //
                // hold_id: a failing confirmation's compensation releases the hold
                // before discarding its intent, so a confirmation in that gap wins
                // the UPDATE and collides with the old intent. Transient.
                //
                // key_hash: two requests with one key confirming different holds;
                // both win their UPDATE and the second reservation collides.
                dbContext.Entry(intent).State = EntityState.Detached;

                if (record is not null)
                {
                    dbContext.Entry(record).State = EntityState.Detached;
                }

                // The violation aborted this transaction (every further statement
                // fails with 25P02), so the recovery reads run after it ends.
                await transaction.RollbackAsync(cancellationToken);

                if (keyHash is not null)
                {
                    CheckoutIdempotencyRecord? byKey = await dbContext.CheckoutIdempotencyRecords.AsNoTracking()
                        .SingleOrDefaultAsync(r => r.KeyHash == keyHash, cancellationToken);

                    if (byKey is not null)
                    {
                        return new ConfirmationStart { ReplayRecord = byKey };
                    }
                }

                // The hold_id collision. The rollback returned the hold to 'held',
                // so a retry shortly succeeds.
                throw new ConflictException(
                    "A previous confirmation for this hold is still being cleaned up. Please try again shortly.");
            }

            await transaction.CommitAsync(cancellationToken);

            return new ConfirmationStart { Intent = intent, Hold = confirmed, Record = record };
        });
    }

    /// <summary>
    ///     Reached when the hold transition finds no 'held' row. Recovers this
    ///     request's own committed-but-unacknowledged attempt, replays a
    ///     completed checkout under the same idempotency key, and otherwise lets
    ///     "the hold is gone" stand.
    /// </summary>
    private async Task<ConfirmationStart> RecoverOwnCommittedAttemptAsync(
        Guid holdId, Guid bookingId, string? keyHash,
        IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        // By this request's own booking id, not the hold id: only an earlier
        // attempt of this invocation could have written an intent under it.
        PendingBookingIntent? own = await dbContext.PendingBookingIntents.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == bookingId, cancellationToken);

        if (own is null)
        {
            // Not ours. A client retrying the same checkout under the same key,
            // whose earlier request won the hold under a different booking id, is
            // answered with a replay; a 404 would report "gone" for a checkout
            // that succeeded.
            if (keyHash is not null)
            {
                CheckoutIdempotencyRecord? byKey = await dbContext.CheckoutIdempotencyRecords.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.KeyHash == keyHash, cancellationToken);

                if (byKey is not null)
                {
                    // The read is the decision, so it runs inside the transaction;
                    // the transaction is ended before the caller issues anything.
                    await transaction.RollbackAsync(cancellationToken);

                    return new ConfirmationStart { ReplayRecord = byKey };
                }
            }

            // Not available to this caller: 404, the same answer as an expired or
            // unknown hold.
            await transaction.RollbackAsync(cancellationToken);
            throw new NotFoundException("Hold", holdId);
        }

        // Our own committed attempt. The intent proves the hold moved with it, so
        // re-confirming would fail the 'held' guard; read the snapshot back.
        ConfirmedHold? hold = await holdConfirmation.GetConfirmedHoldAsync(holdId, cancellationToken);

        if (hold is null)
        {
            // Our intent, but the hold has left 'pending_payment': the reconcile
            // job or the expiry sweep is already unwinding this attempt.
            throw new ConflictException(
                "This booking confirmation was interrupted and is being cleaned up. Please start over.");
        }

        // Nothing was staged in this transaction; the work was committed by the
        // attempt being recovered.
        await transaction.RollbackAsync(cancellationToken);

        // Tracked, because the success path's row-count assertion on the intent
        // delete - what excludes the reconcile job - applies only to a tracked
        // instance.
        dbContext.Attach(own);

        CheckoutIdempotencyRecord? ownRecord = null;

        if (keyHash is not null)
        {
            // Tracked, so the final save can complete the reservation.
            ownRecord = await dbContext.CheckoutIdempotencyRecords
                .SingleOrDefaultAsync(r => r.BookingId == bookingId, cancellationToken);
        }

        return new ConfirmationStart { Intent = own, Hold = hold, Record = ownRecord };
    }

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
        SecureToken.Hash(string.Join('\u001f',
            request.HoldId.ToString(),
            request.GuestName,
            request.GuestEmail,
            request.GuestPhone ?? string.Empty,
            request.PromoCode ?? string.Empty));

    /// <summary>
    ///     Answers a repeated idempotency key: the original booking if there
    ///     is one, and a retryable conflict otherwise.
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

        if (record.CompletedAt is null)
        {
            // Still running, or died mid-flight. A landed attempt is replayed by a
            // later retry; a dead one's reservation is removed by
            // ReconcileOrphanedBookingIntentsJob.
            throw new ConflictException(
                "A confirmation using this Idempotency-Key is still in progress. Please retry shortly.");
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
