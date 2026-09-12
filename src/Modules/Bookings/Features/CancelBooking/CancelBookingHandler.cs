using Microsoft.Extensions.Options;
using Bookings.Entities;
using Bookings.Features.Common;
using Bookings.Outbox;
using Dapper;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Bookings.Serialization;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Time;
using Mediator;
using Outbox;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using Transactions.Contracts;
using Bookings.Contracts;
namespace Bookings.Features.CancelBooking;

public class CancelBookingHandler(
    AppBookingsDbContext dbContext,
    BookingsOutboxDispatcher dispatcher,
    IHoldConfirmation holdConfirmation,
    ITransactionReversal transactionReversal,
    ICurrentUserProvider currentUserProvider,
    IBookingSessions bookingSessions,
    // No BookingLifecyclePolicyOptions any more - the only thing it supplied
    // here was the management token's lifetime, and cancelling no longer sees
    // that token.
    TimeProvider timeProvider) : IRequestHandler<CancelBookingRequest, CancelBookingResponse>
{
    public async ValueTask<CancelBookingResponse> Handle(CancelBookingRequest request, CancellationToken cancellationToken)
    {
        // Doesn't distinguish "doesn't exist" from "isn't yours" - same
        // reasoning as IHostAuthorization.RequireOwnership, now covering
        // two proof-of-ownership paths instead of one: a matching
        // CustomerId (authenticated) or a matching, not-yet-expired
        // management token (guest checkout) - see BookingAccessChecker's
        // own doc comment.
        BookingAccess access = await BookingAccessChecker.ResolveAsync(
                              dbContext, request.BookingId, currentUserProvider.UserId,
                              await bookingSessions.GetSessionBookingIdAsync(cancellationToken), cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        Booking booking = access.Booking;

        // Resolved after the booking loads, from its own snapshotted zone -
        // the refund tier is measured against CheckIn, a property-local date,
        // so a UTC "today" crosses tier boundaries a day early or late
        // depending on which side of UTC the property sits. See
        // docs/adr/0018. This and `cancelledOn` below must stay on the same
        // clock: they feed the same ComputeRefund, and disagreeing across a
        // local midnight would make a recancel report a different figure than
        // the one already queued in ReverseTransactionOutboxMessage.
        DateOnly today = PropertyTimeZone.Today(timeProvider, booking.TimeZoneId);

        // A booking confirmed before cancellation policies existed has no
        // snapshot - falls back to the same default new units get, rather
        // than fabricating a specific claim about what applied
        // retroactively. See Booking.CancellationPolicy's own doc comment.
        //
        // Read unconditionally, not just on the fresh-cancel path below -
        // Cancel() only ever sets BookingStatus, never CancellationPolicy/
        // CheckIn/TotalPrice, so this is exactly as valid on an idempotent
        // recancel, and ComputeRefund below needs it either way.
        CancellationPolicy cancellationPolicy = booking.CancellationPolicy ?? CancellationPolicy.CreateDefault();

        // Shared by the fresh-cancel enqueue below and the recancel fallback
        // further down - same formula, deliberately parameterized on "as of
        // what date" rather than always using `today`, since those two call
        // sites need different anchors (see the recancel branch's own
        // comment for why). Cancelling on or after check-in day itself
        // lands on the same strictest applicable tier as cancelling the
        // moment check-in starts, not an undefined negative day count. The
        // division has to happen first, in plain decimal, before the one
        // Money multiplication - see Money's own doc comment on why
        // `a * b / c` and `a * (b / c)` aren't the same value for a type
        // that rounds on every operation.
        (Money Amount, decimal Percent) ComputeRefund(DateOnly asOf)
        {
            // Reads CheckIn/TotalPrice/CancellationPolicy only, none of which
            // Cancel() touches, so the pre-lock snapshot is as good as the
            // locked one here. Deliberately not moved inside the delegate:
            // recomputing a refund figure per attempt would let two attempts
            // disagree if the clock crossed a tier boundary between them.
            int daysBeforeCheckIn = Math.Max(booking.CheckIn.DayNumber - asOf.DayNumber, 0);
            decimal percent = cancellationPolicy.ResolveRefundPercent(daysBeforeCheckIn);
            return (booking.TotalPrice * (percent / 100m), percent);
        }

        // Idempotent re-cancel skips re-enqueueing the outbox messages a
        // second time - whatever was enqueued on the first Cancel() call is
        // already durable, and OutboxRelayJob keeps retrying it regardless
        // of how many times this endpoint is called. See docs/adr/0003.
        if (booking.BookingStatus != BookingStatus.Cancelled)
        {
            // Eligibility, checked after authorization and separately from
            // it. BookingAccessChecker decided whether this caller may reach
            // the booking at all - a management link stays usable until
            // CheckOut + 90 days, and an authenticated customer's own booking
            // has no time limit whatsoever - and nothing then asked whether
            // cancelling was still a meaningful thing to do. A guest could
            // cancel a stay they were currently in, releasing the remaining
            // nights back to inventory, or cancel one that ended months ago.
            //
            // Not folded into the idempotent branch above: a re-cancel of an
            // already-Cancelled booking stays a no-op success whatever the
            // date, since it changes nothing and the caller is asking for a
            // state that already holds.
            if (!booking.CanBeCancelledOn(today))
            {
                throw new BookingNotCancellableException(booking.Id);
            }

            // Read *before* enqueueing and dispatching, deliberately. Both
            // reads further down are invalidated by this request's own inline
            // dispatch: a ReverseTransactionAsync that lands moves the
            // transaction past Succeeded, so GetSucceededTransactionAmountAsync
            // then reports "nothing to refund" while GetRefundSnapshotAsync
            // starts reporting one. Deciding after the dispatch made the
            // response a function of whether that attempt happened to win -
            // the same request answering RefundPending: false with a figure,
            // or true, depending on a race the caller can't see or control.
            // Kept only to decide whether to report a figure at all - not to
            // decide the figure. A payment that has not committed yet reads as
            // "nothing owed" here while an obligation is about to be written
            // for it, so the response below derives RefundPending from the
            // obligation instead, which is true regardless of where the payment
            // has got to.
            bool refundOwed =
                await transactionReversal.GetSucceededTransactionAmountAsync(booking.Id, cancellationToken) is not null;

            // Last, and only on the branch that actually cancels something.
            //
            // Ordering is the whole of it. Ahead of the eligibility check it
            // would demand a confirmation for a stay that cannot be cancelled
            // at all - the guest types their address and is told 409 anyway.
            // Ahead of the idempotent branch it would refuse a re-cancel that
            // changes nothing. Neither of those is destructive, so neither is
            // what this guards.
            //
            // Nothing is leaked by checking eligibility first: CheckIn,
            // CheckOut and CanCancel are all in the management response the
            // caller could already read.
            RequireGuestEmailForLinkAccess(access, request.GuestEmail);

            (Money refundAmount, decimal refundPercent) = ComputeRefund(today);

            // Everything that must survive a retry is built INSIDE the
            // delegate, and that placement is the whole point rather than a
            // style choice.
            //
            // SaveChangesAsync defaults to acceptAllChangesOnSuccess, so the
            // moment it returns, the mutated Booking and both enqueued
            // OutboxMessages are Unchanged - accepted, even though the
            // transaction has not committed. Built outside, a transient
            // failure on CommitAsync then retried a delegate with nothing left
            // to save: the hold release ran again (it is a statement, not
            // tracked state), the save wrote nothing, the commit succeeded,
            // and the handler returned a cheerful Cancelled response over a
            // database holding a Confirmed booking, a released hold and zero
            // compensating rows. Silent, and it loses a refund.
            //
            // ConfirmBookingHandler has the same outer shape and is safe
            // because its Add/Remove calls sit inside its delegate. This is
            // that shape, applied.
            IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

            CancelOutcome outcome = await strategy.ExecuteAsync(async () =>
            {
                // Nothing inherited from a previous attempt. Without this, a
                // second attempt starts holding the first attempt's accepted
                // entities and reproduces the same bug in a new shape.
                dbContext.ChangeTracker.Clear();

                await using IDbContextTransaction transaction =
                    await dbContext.Database.BeginTransactionAsync(cancellationToken);

                // The booking lock is taken FIRST, before the hold is touched.
                // BookingPaymentConfirmation locks the booking and then marks
                // the hold paid; taking them in the other order here let a
                // concurrent cancel and payment deadlock. Postgres detects it
                // and 40P01 is retriable, so it self-heals - but it self-heals
                // by retrying a whole transaction under contention, which is a
                // cost with no benefit. One order, everywhere: booking, then
                // hold.
                // The id alone, through Dapper, then the entity through EF -
                // not one FromSqlRaw doing both. Booking carries Money as a
                // complex property and EF composes its own projection over a
                // raw query, asking for a "TotalPrice_Amount" column the
                // snake_case convention never produced; `SELECT *` returns the
                // real columns and the composed projection then fails on one
                // that does not exist. ExpireUnpaidBookingsJob and
                // BookingPaymentConfirmation both claim their row this way for
                // exactly this reason. The lock belongs to the transaction
                // either way, so the entity can be read back normally after.
                //
                // FOR UPDATE, not SKIP LOCKED: a cancellation has a caller
                // waiting and must see the committed outcome, where a sweep
                // should step over a contended row and revisit it.
                if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                {
                    DbConnection connection = dbContext.Database.GetDbConnection();

                    await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                        """SELECT id FROM "bookings" WHERE id = @BookingId FOR UPDATE""",
                        new { request.BookingId },
                        transaction.GetDbTransaction(),
                        cancellationToken: cancellationToken));
                }

                Booking? locked = await dbContext.Bookings
                    .SingleOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken);

                // Re-read under the lock rather than reusing the instance
                // BookingAccessChecker returned. That one was read before the
                // transaction existed, so it is stale by the time this runs -
                // and definitely stale on a retry, where it may describe a
                // world the previous attempt already changed.
                if (locked is null)
                {
                    throw new NotFoundException(nameof(Booking), request.BookingId);
                }

                // The ambiguous commit, answered from persisted state rather
                // than inferred. A previous attempt may have committed and
                // lost its acknowledgement, in which case the cancellation
                // already happened and its compensations are already durable -
                // so this is success, not a conflict. Same verify-before-
                // compensate reasoning as ConfirmBookingHandler and
                // OnDeadLetteredAsync (docs/adr/0017).
                if (locked.BookingStatus == BookingStatus.Cancelled)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new CancelOutcome(locked, null, null);
                }

                // Re-checked under the lock for the same reason the re-read
                // exists: eligibility was decided against a stale snapshot.
                if (!locked.CanBeCancelledOn(today))
                {
                    throw new BookingNotCancellableException(locked.Id);
                }

                DateTimeOffset cancelledAt = timeProvider.GetUtcNow();
                locked.Cancel(cancelledAt);

                // The durable refund obligation, in the same transaction as the
                // cancellation. Deliberately says nothing about whether a
                // payment exists - that is what the two paths used to guess at,
                // each declining cases it assumed the other owned.
                //
                // The booking id is the key, so a retried attempt collides
                // rather than writing a second. Discarded and re-added on each
                // attempt because the tracker is cleared at the top of this
                // delegate (docs/adr/0025).
                dbContext.RefundObligations.Add(new RefundObligation
                {
                    BookingId = locked.Id,
                    CancelledAt = cancelledAt,
                    PolicyRefundAmount = refundAmount.Amount,
                    Currency = refundAmount.Currency,
                    Cause = BookingCancellationCause.GuestCancellation
                });

                // Enqueued here, per attempt. These are the rows whose absence
                // made the old failure silent: the response promises a pending
                // refund on the strength of them existing, and the relay
                // backstop can only deliver rows that were written.
                OutboxMessage reverseTransactionRow = dispatcher.Enqueue(
                    // Just the booking id. Everything the resolver needs is on
                    // the obligation written above, in this same transaction -
                    // repeating the amount here would be a second copy that can
                    // disagree with the row that decides.
                    new ReverseTransactionOutboxMessage(locked.Id),
                    BookingsJsonSerializerContext.Default.ReverseTransactionOutboxMessage);
                OutboxMessage reverseRedemptionRow = dispatcher.Enqueue(
                    new ReverseRedemptionOutboxMessage(locked.Id),
                    BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage);

                // Joins the transaction rather than autocommitting - see
                // HoldConfirmation.AmbientTransaction. After the booking lock,
                // never before it.
                await holdConfirmation.ReleaseHoldAsync(locked.HoldId, cancellationToken);

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return new CancelOutcome(locked, reverseTransactionRow, reverseRedemptionRow);
            });

            // Dispatched after the commit, and only for an attempt that
            // actually enqueued. A recovered ambiguous commit has rows from
            // its own earlier attempt, already dispatched or waiting for the
            // relay.
            if (outcome.ReverseTransactionRow is not null)
            {
                await dispatcher.TryDispatchAsync(outcome.ReverseTransactionRow, cancellationToken);
            }

            if (outcome.ReverseRedemptionRow is not null)
            {
                await dispatcher.TryDispatchAsync(outcome.ReverseRedemptionRow, cancellationToken);
            }

            // Always pending on a fresh cancel, whatever the inline dispatch
            // just did. The durable outbox row is the guarantee; dispatching
            // inline is a latency optimisation, and letting its outcome reach
            // the response gave callers two different shapes for one action.
            // A caller now has exactly one path here: a figure and
            // RefundPending: true, or no refund at all. What actually landed
            // is reported by a later re-cancel, off settled state.
            // Built from the booking the delegate committed, never the one
            // read before the transaction opened - reporting a cancellation
            // off an entity nothing verified is how the old failure managed
            // to look like success.
            // Still gated on a succeeded payment, and deliberately NOT on "an
            // obligation exists and is unresolved".
            //
            // An obligation is written for every cancellation, including the
            // overwhelming majority with no payment behind them at all, so
            // reporting a pending refund whenever one exists would promise
            // money back to every guest who cancels an unpaid booking.
            //
            // What that leaves is a narrow snapshot problem: a payment
            // committing after this read is reported here as "no refund" and
            // then refunded anyway by the resolver. That is a response being a
            // point-in-time answer rather than a wrong one - the obligation
            // guarantees the refund happens, and a later re-cancel reports it
            // off settled state.
            if (!refundOwed)
            {
                return BuildResponse(
                    outcome.Booking, refundAmount: null, currency: null, refundPercent: null, refundPending: false);
            }

            // The persisted figure when there is one, the computed one only as
            // a fallback - because this handler's policy percentage is no
            // longer guaranteed to be what gets refunded.
            //
            // A payment that succeeded *after* this cancellation bought
            // nothing, so the whole amount goes back and the reversal above
            // declines in favour of the confirmation path (see
            // Transaction.RefundOwedIsThisPathsToWrite). Reporting the policy
            // percentage there would hand the guest a number the database is
            // about to contradict.
            //
            // Read after the dispatch, unlike refundOwed above, and for the
            // opposite reason: refundOwed must not depend on whether the inline
            // attempt won, whereas this is precisely "what did it decide". A
            // miss is the ordinary "not settled yet" case - the durable outbox
            // row is the guarantee and the relay will deliver it - so the
            // computed figure stands in, still flagged RefundPending.
            TransactionRefundSnapshot? settled =
                await transactionReversal.GetRefundSnapshotAsync(outcome.Booking.Id, cancellationToken);

            if (settled is not null)
            {
                return BuildResponse(
                    outcome.Booking,
                    settled.RefundAmount.Amount,
                    settled.RefundAmount.Currency,
                    settled.Amount.Amount == 0m ? null : settled.RefundAmount.Amount / settled.Amount.Amount * 100m,
                    refundPending: settled.RefundPending);
            }

            return BuildResponse(
                outcome.Booking, refundAmount.Amount, refundAmount.Currency, refundPercent, refundPending: true);
        }

        // Everything below serves the idempotent re-cancel only, so these are
        // reads of state that settled in some earlier request rather than a
        // read-back of this one's own writes.
        //
        // Checked first, preferred over anything computed below - the
        // authoritative record of what was actually applied when the
        // reversal ran, whenever that was. Once this exists, the
        // transaction has moved past Succeeded, so a Succeeded-only check
        // below would misread it as "nothing to refund" - a genuinely
        // refunded booking reporting no refund on an idempotent recancel.
        // RefundPercent is derived from the snapshot's own ratio, not
        // resolved fresh, so it always matches what actually landed.
        TransactionRefundSnapshot? refundSnapshot =
            await transactionReversal.GetRefundSnapshotAsync(booking.Id, cancellationToken);

        if (refundSnapshot is not null)
        {
            // Guarded rather than assumed. Transaction.Create's own
            // Guard.Against.NegativeOrZero means Amount is never zero here,
            // so this is unreachable today - but that invariant lives in
            // another module, and decimal division by zero throws
            // DivideByZeroException rather than yielding infinity, so an
            // unguarded ratio would turn a future relaxation of that guard
            // into a 500 on a cancellation. Reporting a null percent
            // alongside a real amount is the honest answer if the
            // denominator ever is zero.
            decimal? refundPercent = refundSnapshot.Amount.Amount == 0m
                ? null
                : refundSnapshot.RefundAmount.Amount / refundSnapshot.Amount.Amount * 100m;

            // Currency comes off the snapshot itself now. It used to be paired
            // back on here from booking.TotalPrice - the same currency, but
            // asserted at the call site rather than carried by the value, in
            // the one place getting it wrong costs real money.
            // From the snapshot, not hardcoded false. A snapshot exists as
            // soon as MarkRefundPending runs, and this used to report every
            // one of those as settled - so a guest re-checking a cancellation
            // whose refund was still queued was told the money had gone back
            // when it had only been asked for.
            return BuildResponse(
                booking, refundSnapshot.RefundAmount.Amount, refundSnapshot.RefundAmount.Currency,
                refundPercent, refundPending: refundSnapshot.RefundPending);
        }

        // No snapshot yet - either there was never anything to refund, or a
        // refund is queued but hasn't reached the refund sub-lifecycle at
        // all (the inline dispatch attempt, this request's own or an
        // earlier recancel's, failed or hasn't run). The only way to tell
        // these apart deterministically, independent of whether dispatch
        // happens to land inline.
        bool hasSucceededTransaction =
            await transactionReversal.GetSucceededTransactionAmountAsync(booking.Id, cancellationToken) is not null;

        if (!hasSucceededTransaction)
        {
            return BuildResponse(booking, refundAmount: null, currency: null, refundPercent: null, refundPending: false);
        }

        // A Succeeded transaction exists but nothing has reversed it yet -
        // a refund is guaranteed, but the figure still has to be resolved
        // here since no snapshot exists yet.
        //
        // Anchored to when this booking was actually cancelled, not
        // `today` - on a fresh cancel those are the same moment, but on a
        // recancel whose earlier dispatch hasn't landed, `today` could
        // cross a tier boundary since the original cancel and disagree
        // with the amount already baked into the queued
        // ReverseTransactionOutboxMessage payload. booking.ModifiedAt
        // reflects the SaveChangesAsync that ran Cancel() - nothing else
        // touches a Cancelled booking - so it's a reliable stand-in for
        // the original cancellation date.
        DateOnly cancelledOn = PropertyTimeZone.ToLocalDate(
            booking.ModifiedAt ?? timeProvider.GetUtcNow(), booking.TimeZoneId);
        (Money pendingRefundAmount, decimal pendingRefundPercent) = ComputeRefund(cancelledOn);

        return BuildResponse(
            booking, pendingRefundAmount.Amount, pendingRefundAmount.Currency, pendingRefundPercent, refundPending: true);
    }

    /// <summary>
    ///     What the retried delegate produced: the booking as actually
    ///     committed, and the compensating rows to dispatch - null when this
    ///     attempt recovered an earlier one's commit rather than making its
    ///     own, since those rows already exist and are already in flight.
    /// </summary>
    private sealed record CancelOutcome(
        Booking Booking, OutboxMessage? ReverseTransactionRow, OutboxMessage? ReverseRedemptionRow);

    /// <summary>
    ///     The second factor on the destructive action - see
    ///     CancelBookingRequest.GuestEmail for why it exists and why it does
    ///     not apply to account holders.
    /// </summary>
    private static void RequireGuestEmailForLinkAccess(BookingAccess access, string? supplied)
    {
        if (access.Kind != BookingAccessKind.Link)
        {
            return;
        }

        // Case-insensitive and trimmed. Domains are case-insensitive by
        // definition and no mail provider in practice treats the local part
        // otherwise, so rejecting "Jane@Example.com" would be refusing the
        // right answer typed in the wrong case - a false negative on a
        // confirmation step, which teaches people the field is broken rather
        // than teaching them to check it.
        //
        // An ordinary comparison rather than a fixed-time one, deliberately.
        // This is a confirmation that the caller already knows the booking,
        // not a secret being verified: the link they hold is the credential,
        // and the endpoint's rate limit is what bounds guessing at the
        // address. A fixed-time compare would imply a threat model this field
        // does not have, while still leaking length.
        if (!string.IsNullOrWhiteSpace(supplied)
            && string.Equals(supplied.Trim(), access.Booking.GuestEmail?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Keyed to the field so a client can show it against the input, the
        // same shape ConfirmBookingHandler uses for a rejected promo code.
        // Says only that it did not match: a caller who guessed wrong learns
        // nothing about the real address.
        throw new ValidationException(
            nameof(CancelBookingRequest.GuestEmail),
            "Enter the email address this booking was made with to confirm the cancellation.");
    }

    private static CancelBookingResponse BuildResponse(
        Booking booking,
        decimal? refundAmount,
        Currency? currency,
        decimal? refundPercent,
        bool refundPending) =>
        new CancelBookingResponse
        {
            BookingId = booking.Id,
            BookingStatus = booking.BookingStatus,
            RefundAmount = refundAmount,
            Currency = currency,
            RefundPercent = refundPercent,
            RefundPending = refundPending
        };
}
