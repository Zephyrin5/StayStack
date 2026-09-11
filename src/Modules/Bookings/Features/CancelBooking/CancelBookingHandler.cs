using Microsoft.Extensions.Options;
using Bookings.Entities;
using Bookings.Features.Common;
using Bookings.Outbox;
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

            booking.Cancel();

            (Money refundAmount, decimal refundPercent) = ComputeRefund(today);

            // Two messages now, not three. The hold release used to be one of
            // them because the hold lived in another module and another
            // DbContext, so an outbox row was the only way to make "cancel the
            // booking" and "release its inventory" eventually agree. They are
            // now the same DbContext, and a plain call inside the transaction
            // below makes them agree immediately instead of eventually -
            // no row, no dispatch, no relay, no window in which a Cancelled
            // booking still blocks its range.
            //
            // The other two stay on the outbox because they genuinely cross a
            // module boundary (Transactions, Promotions) and cannot join this
            // transaction. Each is independent: ReverseRedemptionAsync is a
            // no-op when there is nothing to reverse, and
            // ReverseTransactionAsync's own no-op case (nothing Succeeded to
            // reverse) is exactly as safe, and simpler, to let it discover on
            // its own than to skip enqueueing it here.
            //
            // Enqueued before the strategy delegate rather than inside it. A
            // retried attempt re-runs the release and the save - both
            // idempotent, the release because a rolled-back transaction undid
            // it - but re-running Enqueue would add a second copy of each row
            // to the tracker, and the rows are still Added from the failed
            // attempt.
            OutboxMessage reverseTransactionRow = dispatcher.Enqueue(
                new ReverseTransactionOutboxMessage(booking.Id, refundAmount.Amount, refundAmount.Currency),
                BookingsJsonSerializerContext.Default.ReverseTransactionOutboxMessage);
            OutboxMessage reverseRedemptionRow = dispatcher.Enqueue(
                new ReverseRedemptionOutboxMessage(booking.Id), BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage);

            IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

            await strategy.ExecuteAsync(async () =>
            {
                await using IDbContextTransaction transaction =
                    await dbContext.Database.BeginTransactionAsync(cancellationToken);

                // Joins the transaction rather than autocommitting - see
                // HoldConfirmation.AmbientTransaction. Ordering within it no
                // longer matters, which is the point: either both the
                // cancellation and the release land or neither does.
                await holdConfirmation.ReleaseHoldAsync(booking.HoldId, cancellationToken);

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            });

            await dispatcher.TryDispatchAsync(reverseTransactionRow, cancellationToken);
            await dispatcher.TryDispatchAsync(reverseRedemptionRow, cancellationToken);

            // Always pending on a fresh cancel, whatever the inline dispatch
            // just did. The durable outbox row is the guarantee; dispatching
            // inline is a latency optimisation, and letting its outcome reach
            // the response gave callers two different shapes for one action.
            // A caller now has exactly one path here: a figure and
            // RefundPending: true, or no refund at all. What actually landed
            // is reported by a later re-cancel, off settled state.
            return refundOwed
                ? BuildResponse(booking, refundAmount.Amount, refundAmount.Currency, refundPercent, refundPending: true)
                : BuildResponse(booking, refundAmount: null, currency: null, refundPercent: null, refundPending: false);
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
