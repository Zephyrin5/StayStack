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
        // Chosen up front: a redeemed promo code needs this id to write its
        // PromotionRedemption row before the Booking itself is ever saved, the
        // intent row below is keyed by it, and - because it's pre-generated -
        // any failed save can ask the database whether the Booking actually
        // committed rather than inferring it from an exception type.
        // Before anything else, and deliberately outside every transaction
        // below: a replay must not begin a confirmation at all.
        // Checked here rather than in the validator, because the endpoint
        // assigns this after validation has run - see the property's comment.
        //
        // The floor is the point. Replaying a key returns a live management
        // token, so a caller sending a counter has misunderstood what they are
        // holding, and two guests colliding on "1" would mean handing one of
        // them the other's booking. The fingerprint check below is what makes
        // a guessed key useless in practice; this makes the misunderstanding
        // loud instead of latent. 16 admits a UUID and any sensible random
        // token, and excludes a counter.
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

        // Transaction A. The hold transition and the marker saying "a
        // confirmation for this hold began" now commit together, which is what
        // makes an ambiguous commit answerable: the intent is present if and
        // only if the hold moved. Before, they were two independent commits,
        // so a connection lost between them left a hold in 'pending_payment'
        // with nothing recording that it had been claimed - the reconcile job
        // had no row to find, and the range stayed blocked until someone
        // noticed. They were only ever separable because they lived in
        // different modules.
        ConfirmationStart start = await BeginConfirmationAsync(
            request.HoldId, bookingId, keyHash, requestFingerprint, cancellationToken);

        // The race the top-of-handler read cannot close: a concurrent request
        // carrying the same key finished between that read and our insert.
        if (start.Replay is not null)
        {
            return start.Replay;
        }

        // Non-null whenever Replay is null - the two are the result's two
        // arms, and BeginConfirmationAsync returns one or the other.
        PendingBookingIntent intent = start.Intent!;
        ConfirmedHold hold = start.Hold!;
        CheckoutIdempotencyRecord? idempotencyRecord = start.Record;

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

            // Frees the key so the client's retry can start a fresh
            // confirmation, since this one left nothing to replay.
            //
            // Filtered on CompletedAt == null, which is not belt and braces.
            // This same helper runs on the verify-before-compensate path where
            // the booking *did* commit - and there the record is completed, in
            // that very transaction. Deleting it there would destroy the reply
            // for precisely the case the feature exists for: the guest whose
            // connection dropped after the commit.
            await dbContext.CheckoutIdempotencyRecords
                .Where(r => r.BookingId == bookingId && r.CompletedAt == null)
                .ExecuteDeleteAsync(cancellationToken);

            if (idempotencyRecord is not null)
            {
                dbContext.Entry(idempotencyRecord).State = EntityState.Detached;
            }
        }

        // The compensating procedure, written once. All three failure paths
        // below run exactly this - enqueue, save, dispatch, discard - and the
        // ordering within it took several passes to get right, which is
        // precisely why having it spelled out three times was a liability: a
        // future edit to one copy diverges from the other two silently, and
        // every copy reads as deliberate because each is internally
        // consistent.
        //
        // The order is the whole content of this function:
        //
        //  - Enqueue then save, so the compensations are durable rows before
        //    anything acts on them (docs/adr/0003). Dispatching is a best
        //    effort on top; OutboxRelayJob delivers whatever this misses.
        //  - Save before DiscardIntentAsync, never after. The intent is the
        //    marker that says "a confirmation started and did not finish", so
        //    discarding it first would remove the safety net before the
        //    replacement was durable. A crash between the two instead leaves
        //    the intent alive and lets the reconcile job repeat these
        //    compensations, which are idempotent by construction.
        //  - DiscardIntentAsync last, which also means its ExecuteDelete is
        //    registered after the caller's ChangeTracker.Clear() where there
        //    is one, and its detach runs after the delete rather than before.
        //
        // reverseRedemption is the only thing that varies between the three
        // callers, so it is the only parameter.
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
            // A redeemed code is exclusive of the length-of-stay discount
            // rather than stacking with it - the coupon applies against the
            // rate-adjusted subtotal (LOS discount undone), not the
            // LOS-discounted total. See PricingCalculator for why this is
            // the one PricingRule type a coupon competes with rather than
            // compounds. hold.Subtotal is read directly, not reconstructed
            // via TotalPrice + LengthOfStayDiscountAmount - that
            // reconstruction is exactly the rounding bug docs/adr/0015
            // exists to close.
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
                // A genuinely broken code - RedeemAsync itself failed, so it
                // never created a redemption to reverse. The hold is
                // already 'booked' and nothing else will ever confirm it
                // into a real Booking - releasing it means the guest has to
                // re-hold, the correct cost for a code that was never
                // valid. Enqueued via the outbox (docs/adr/0003), not a
                // direct call that could be silently lost.
                // Reversed even though RedeemAsync threw and may never have
                // created a redemption to reverse: ReverseRedemptionAsync is
                // a no-op when there is nothing outstanding, and guessing
                // wrong the other way would leave a single-use code burned.
                await CompensateAsync(reverseRedemption: true);

                if (redemptionException is PromotionInvalidException promotionInvalidException)
                {
                    // Bare nameof(), like every other ValidationException.
                    // This used to camelCase the key itself, because
                    // ValidationProblemDetails.Errors is a dictionary and
                    // PropertyNamingPolicy doesn't reach dictionary keys -
                    // true, but a rule only two throw sites in the codebase
                    // remembered. GlobalExceptionHandler.BuildValidationProblem
                    // now converts every key on the way out.
                    throw new ValidationException(
                        nameof(request.PromoCode),
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
            // Compensates exactly like the redemption-failure branch above:
            // release the hold AND reverse the redemption. Both are
            // post-ConfirmHoldAsync failures of the same operation, so they
            // owe the same cleanup.
            //
            // This branch used to reverse the redemption only, on the
            // reasoning that the code was valid and the guest did nothing
            // wrong, so "the hold stays 'booked' so a retried Confirm can
            // still use it". That premise is false: ConfirmHoldAsync updates
            // WHERE status = 'held', so a 'booked' hold yields no row and a
            // retry gets NotFoundException. The hold was not being preserved
            // for the guest, it was being stranded - and nothing else would
            // ever collect it. ExpiredHoldsSweepJob only deletes
            // status = 'held'; ReconcileOrphanedBookingIntentsJob works from
            // intents, which DiscardIntentAsync below then removes; and the
            // job that used to scan for booked holds with no booking is gone
            // (docs/adr/0017). The unit's dates would be blocked forever,
            // with no row anywhere pointing at them - reached by nothing more
            // exotic than a guest typing a coupon that doesn't beat their
            // length-of-stay discount.
            //
            // Releasing does cost the guest their 15-minute window, since
            // ReleaseHoldAsync resets hold_expires_at to now. That is the
            // right trade against blocking the dates permanently, and it is
            // recoverable: the expired row no longer blocks a re-hold, because
            // HoldAvailabilityHandler's own per-unit cleanup DELETE removes
            // stale 'held' rows before its INSERT.
            //
            // <= 0 as well as the no-savings case above. ComputeDiscountAmount
            // caps a discount at the subtotal, so a 100% code - or any
            // FixedAmount code at least as large - lands exactly on zero,
            // which `>= hold.TotalPrice` does not catch (0 >= 300 is false).
            // A zero-total booking is unpayable: Transaction.Create refuses
            // it, so the guest would be left holding a Pending booking that
            // fails every payment attempt. Booking.Create enforces the same
            // invariant; this branch exists so the guest gets a real message
            // rather than a domain guard's.
            if (discountedPrice.Amount <= 0m || discountedPrice.Amount >= hold.TotalPrice.Amount)
            {
                // The redemption succeeded here, so it definitely needs
                // reversing - the code was consumed by a booking that is
                // about to be refused.
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
                unit.CancellationPolicy,
                unit.TimeZoneId,
                // The hold is already 'pending_payment' by this point, which
                // means this unit is off the market. This is the deadline
                // that gives it back: ExpireUnpaidBookingsJob cancels the
                // booking and releases the hold once it passes.
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

            // Completed in the same SaveChangesAsync as the Booking insert, so
            // the record says "there is a booking to replay" if and only if
            // there is one. Writing it afterwards would reintroduce, one level
            // up, exactly the lost-acknowledgement gap this feature exists to
            // close: a committed booking whose replay record never landed is a
            // guest stranded by the mechanism meant to rescue them.
            if (idempotencyRecord is not null)
            {
                // CompletedAt only. The plaintext management token used to be
                // written here too, which undid the whole point of storing
                // only SecureToken.Hash in booking_management_tokens: one
                // table read yielded live bearer credentials for every guest
                // checkout inside the replay window. A leaked URL exposes one
                // booking; a leaked backup exposed all of them. Replay mints a
                // fresh token instead - see ReplayAsync.
                idempotencyRecord.CompletedAt = timeProvider.GetUtcNow();
            }

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
            // Only if a code was actually redeemed - unlike the two branches
            // above, this path is reached whether or not one was.
            await CompensateAsync(reverseRedemption: redeemedDiscountAmount is not null);

            // The original failure, preserved - now that compensating is a
            // durable local write rather than two independent cross-module
            // calls that could each fail unpredictably, there's no second
            // failure mode left here worth an AggregateException for.
            throw;
        }

        return BuildResponse(booking, managementToken);
    }

    /// <summary>
    ///     Transaction A: writes the durable marker that this confirmation has
    ///     begun and transitions the hold ('held' -> 'pending_payment') in one
    ///     commit. Returns the tracked intent the success path later removes,
    ///     and the hold's price snapshot.
    ///     <para>
    ///         Price/currency come from the hold's own snapshot, not a fresh
    ///         unit lookup - the price a customer saw when they held is the
    ///         price they get, even if the unit's base price changed since.
    ///     </para>
    ///     <para>
    ///         A failure anywhere inside rolls back both, so there is no
    ///         compensation to run and nothing for the reconcile job to find -
    ///         which is why the caller no longer discards the intent when the
    ///         hold transition fails. The cross-module work that genuinely
    ///         cannot join a transaction (the redemption, in Promotions) still
    ///         happens afterwards on the outbox, exactly as before.
    ///     </para>
    /// </summary>
    /// <summary>
    ///     Either a confirmation that has begun - <see cref="Intent"/> and
    ///     <see cref="Hold"/> set - or a finished response to return instead
    ///     of beginning one. Exactly one arm is populated.
    /// </summary>
    private sealed record ConfirmationStart
    {
        public ConfirmBookingResponse? Replay { get; init; }
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
            // Each attempt starts from a clean tracker. A retry following a
            // committed-but-unacknowledged SaveChanges would otherwise Add a
            // second instance carrying the same key as the one already tracked
            // Unchanged, and fail on the identity map rather than on anything
            // real. Safe here specifically because this is the first database
            // work the handler does.
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // The hold transition goes FIRST, and the ordering is the whole
            // arbitration story.
            //
            // ExecuteConfirmAsync is a conditional UPDATE - `WHERE id = @HoldId
            // AND status = 'held' AND hold_expires_at > @Now` - so it is already
            // an exactly-one-winner compare-and-set on the contended row. Doing
            // it before the intent insert lets it decide every race, because a
            // second transaction's UPDATE blocks on the winner's row lock and
            // then re-evaluates that WHERE against the committed row: 'held' is
            // gone, no row comes back, and the loser never reaches the intent
            // table at all.
            //
            // Inserting the intent first, as this used to, made the intent's
            // unique index on hold_id the arbiter instead - deciding a race
            // about a hold by contending on a marker that points at it. That
            // worked, but it answered second-hand ("somebody else has an intent
            // for this") where the UPDATE answers directly ("this hold is no
            // longer available"), and it forced a recovery path to tell three
            // situations apart using the age of the other request's intent row.
            //
            // Joins the transaction rather than autocommitting - see
            // HoldConfirmation.AmbientTransaction.
            ConfirmedHold confirmed;

            try
            {
                confirmed = await holdConfirmation.ConfirmHoldAsync(holdId, cancellationToken);
            }
            catch (NotFoundException)
            {
                // No row matched. Three ways to get here, and only the last is
                // this request's own doing:
                //
                //  - Another confirmation took this hold. Correct answer: the
                //    hold is gone, which is what NotFoundException already says.
                //  - The hold expired, or never existed. Same answer.
                //  - This delegate already ran, committed, and lost its
                //    acknowledgement to an execution-strategy retry - so the
                //    'held' row this attempt is looking for was consumed by its
                //    own previous attempt.
                //
                // The pre-generated bookingId separates the last from the other
                // two: only our own attempt could have written an intent under
                // it. Nothing has been staged in this transaction yet, which is
                // why the UPDATE running first also makes this recovery simple -
                // there is no half-built state to unpick.
                return await RecoverOwnCommittedAttemptAsync(
                    holdId, bookingId, keyHash, requestFingerprint, transaction, cancellationToken);
            }

            PendingBookingIntent intent = new PendingBookingIntent
            {
                Id = bookingId,
                HoldId = holdId,
                CreatedAt = timeProvider.GetUtcNow()
            };

            dbContext.PendingBookingIntents.Add(intent);

            // Reserved in the same transaction as the hold transition rather
            // than written on the way out, so a rollback frees the key and the
            // record is present if and only if the confirmation began. See
            // docs/adr/0022.
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
                when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Two indexes can fire here, and neither means a race for the
                // hold - the UPDATE above already settled that.
                //
                // The intent's index on hold_id fires in one narrow window: a
                // failing confirmation's CompensateAsync releases its hold back
                // to 'held' and only then discards its intent, so between those
                // two statements the hold is available while its old intent is
                // still there. A confirmation arriving in that gap legitimately
                // wins the UPDATE and then collides. Transient, and the remedy
                // is to try again in a moment.
                //
                // The key's index fires when two requests carrying one
                // idempotency key are confirming *different* holds - both win
                // their own UPDATE, and the second one's reservation collides.
                dbContext.Entry(intent).State = EntityState.Detached;

                if (record is not null)
                {
                    dbContext.Entry(record).State = EntityState.Detached;
                }

                // The violation aborted this transaction - Postgres fails every
                // further statement on it with 25P02 until it ends - so the
                // recovery's reads cannot run inside it.
                await transaction.RollbackAsync(cancellationToken);

                if (keyHash is not null)
                {
                    CheckoutIdempotencyRecord? byKey = await dbContext.CheckoutIdempotencyRecords.AsNoTracking()
                        .SingleOrDefaultAsync(r => r.KeyHash == keyHash, cancellationToken);

                    if (byKey is not null)
                    {
                        return new ConfirmationStart
                        {
                            Replay = await ReplayAsync(byKey, requestFingerprint, cancellationToken)
                        };
                    }
                }

                // The hold_id collision, then. One message, where there used to
                // be two chosen by dating the other intent against
                // PendingBookingIntent.ReconcileGrace - that comparison existed
                // to tell "another confirmation is running right now" from "one
                // died and is being cleaned up", and the first of those is no
                // longer something this code can be looking at. The rollback
                // above put the hold back to 'held', so trying again shortly
                // genuinely works.
                throw new ConflictException(
                    "A previous confirmation for this hold is still being cleaned up. Please try again shortly.");
            }

            await transaction.CommitAsync(cancellationToken);

            return new ConfirmationStart { Intent = intent, Hold = confirmed, Record = record };
        });
    }

    /// <summary>
    ///     Reached when the hold transition finds no 'held' row. Recovers this
    ///     request's own committed-but-unacknowledged attempt, and otherwise
    ///     lets the "hold is gone" answer stand.
    ///     <para>
    ///         One branch, where there used to be three. The other two - "a
    ///         confirmation for this hold is already in progress" and "a
    ///         previous one was interrupted and is being cleaned up" - were
    ///         only ever reachable because the intent's unique index arbitrated
    ///         races, which meant reading another request's intent and dating
    ///         it against a grace period to guess which situation produced it.
    ///         The conditional UPDATE decides those races now, so both are
    ///         states this code can no longer be in.
    ///     </para>
    /// </summary>
    private async Task<ConfirmationStart> RecoverOwnCommittedAttemptAsync(
        Guid holdId, Guid bookingId, string? keyHash, string requestFingerprint,
        IDbContextTransaction transaction, CancellationToken cancellationToken)
    {
        // Keyed on this request's own pre-generated bookingId, not on holdId.
        // That is the whole test: only an earlier attempt of *this* invocation
        // could have written an intent under an id generated in this
        // invocation, so a hit means "I already did this" and a miss means
        // somebody else has the hold - or nobody does and it simply expired.
        PendingBookingIntent? own = await dbContext.PendingBookingIntents.AsNoTracking()
            .SingleOrDefaultAsync(i => i.Id == bookingId, cancellationToken);

        if (own is null)
        {
            // Not ours. Before giving up, one case remains worth answering
            // precisely: a client retrying the same checkout under the same
            // idempotency key, whose earlier attempt won the hold under a
            // different bookingId. Replaying that is the entire promise of
            // ADR-0022, and answering 404 instead would report "gone" for a
            // checkout that succeeded.
            if (keyHash is not null)
            {
                CheckoutIdempotencyRecord? byKey = await dbContext.CheckoutIdempotencyRecords.AsNoTracking()
                    .SingleOrDefaultAsync(r => r.KeyHash == keyHash, cancellationToken);

                if (byKey is not null)
                {
                    return new ConfirmationStart
                    {
                        Replay = await ReplayAsync(byKey, requestFingerprint, cancellationToken)
                    };
                }
            }

            // The hold is genuinely not available to this caller. NotFound,
            // the same answer an expired or nonexistent hold gets, because a
            // caller can do exactly one thing about any of them.
            throw new NotFoundException("Hold", holdId);
        }

        // Our own attempt, committed. Its intent proves the hold moved with it
        // - they share a transaction - so re-confirming would fail the
        // status = 'held' guard and abandon a confirmation that had in fact
        // succeeded. Read the snapshot back instead.
        ConfirmedHold? hold = await holdConfirmation.GetConfirmedHoldAsync(holdId, cancellationToken);

        if (hold is null)
        {
            // The intent is ours but the hold is no longer in
            // 'pending_payment' - something (the reconcile job, the expiry
            // sweep) has already begun unwinding this attempt. Nothing here
            // can safely continue on top of that.
            throw new ConflictException(
                "This booking confirmation was interrupted and is being cleaned up. Please start over.");
        }

        // Nothing was staged in this transaction: the UPDATE returned no rows
        // and the inserts are downstream of it. Ending it explicitly rather
        // than committing an empty one, since the work it would have done was
        // already committed by the attempt this is recovering.
        await transaction.RollbackAsync(cancellationToken);

        // Re-attaching is not cosmetic: the success path's row-count assertion
        // on the delete - the structural guarantee that this confirmation owned
        // the intent it removed - only runs against a tracked instance.
        dbContext.Attach(own);

        CheckoutIdempotencyRecord? ownRecord = null;

        if (keyHash is not null)
        {
            // Tracked for the same reason, so Transaction B can complete the
            // reservation this attempt's earlier run reserved.
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
    ///         Covers exactly the fields that decide what gets booked and for
    ///         whom. The unit-separated join is not cosmetic: without a
    ///         separator, ("ab", "c") and ("a", "bc") hash identically, and
    ///         guest names and emails are adjacent free text.
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
            // Deliberately says nothing about the booking behind the key. A
            // caller who reached here either has a client bug or is guessing
            // keys, and the two get the same answer.
            throw new ConflictException(
                "This Idempotency-Key was already used for a different request. Use a new key, or resend the original request unchanged.");
        }

        // The window, enforced on the path that actually hands the credential
        // back. It used to live only in PurgeReplayedCheckoutsJob's DELETE,
        // which means a window that existed only as a background job's
        // behaviour: stop that job, break its cron, or let it fail quietly,
        // and replay kept working indefinitely, returning a management token
        // of unbounded age. A window nothing checks is not a window - the
        // purge is cleanup now, not enforcement.
        if (timeProvider.GetUtcNow() - record.CreatedAt > TimeSpan.FromHours(bookingLifecycle.Value.CheckoutReplayWindowHours))
        {
            throw new ConflictException(
                "This Idempotency-Key has expired. Please start over.");
        }

        if (record.CompletedAt is null)
        {
            // The first attempt is still running, or died mid-flight. Either
            // way there is nothing to replay yet: if it lands, a later retry
            // replays it; if it died, ReconcileOrphanedBookingIntentsJob
            // removes the reservation and a later retry starts over.
            throw new ConflictException(
                "A confirmation using this Idempotency-Key is still in progress. Please retry shortly.");
        }

        Booking? booking = await dbContext.Bookings.AsNoTracking()
            .SingleOrDefaultAsync(b => b.Id == record.BookingId, cancellationToken);

        if (booking is null)
        {
            // Completed but the booking is gone - only reachable if something
            // hard-deleted it, which nothing does. Refuses rather than
            // inventing an answer.
            throw new ConflictException(
                "The booking this Idempotency-Key refers to no longer exists. Please start over.");
        }

        // A new token, minted now, rather than one that was stored.
        //
        // Storing the plaintext was the only way to hand back the *same*
        // credential, and it cost the property that makes these tokens safe at
        // rest: booking_management_tokens deliberately holds nothing but
        // SecureToken.Hash, so the database cannot produce a working
        // credential. Keeping a plaintext copy for the replay window undid
        // that for every guest checkout in it.
        //
        // Nothing requires the replayed token to be the same one. Nothing
        // caps tokens per booking, BookingAccessChecker matches on hash so
        // several valid tokens work unchanged, and replay is rare enough that
        // the extra row is immaterial. No key management, no rotation story,
        // no decrypt path - the alternative, encrypting at rest, buys
        // identical semantics and costs shared key storage plus rotation for a
        // multi-instance deployment.
        //
        // Null for an authenticated caller, decided from the booking rather
        // than from a stored flag: they were never issued one, because their
        // account is what proves ownership.
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

        // The booking itself is re-read rather than stored. Strict idempotency
        // would replay the original response verbatim, but everything in it
        // can go stale: a booking cancelled between the original request and
        // the replay would otherwise be reported as Pending, and the client
        // would act on it. Reporting settled state is the same choice
        // CancelBookingHandler's recancel branch makes.
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
