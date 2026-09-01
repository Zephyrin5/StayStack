using Bookings.Entities;
using Bookings.Features.Common;
using Bookings.Outbox;
using Bookings.Serialization;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Time;
using Mediator;
using Outbox;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using Transactions.Contracts;
namespace Bookings.Features.CancelBooking;

public class CancelBookingHandler(
    AppBookingsDbContext dbContext,
    BookingsOutboxDispatcher dispatcher,
    ITransactionReversal transactionReversal,
    ICurrentUserProvider currentUserProvider,
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
        Booking booking = await BookingAccessChecker.ResolveAsync(
                              dbContext, request.BookingId, currentUserProvider.UserId, request.ManagementToken, timeProvider, cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), request.BookingId);

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

            booking.Cancel();

            (Money refundAmount, decimal refundPercent) = ComputeRefund(today);

            // Enqueued in the same SaveChangesAsync as booking.Cancel() -
            // atomic with the cancellation itself, then dispatched inline so
            // the common case still completes within this request. Each
            // message is independent: ReverseRedemptionAsync/
            // ReleaseHoldAsync are no-ops when there's nothing to reverse/
            // release, and ReverseTransactionAsync's own no-op case (nothing
            // Succeeded to reverse) is exactly as safe (and simpler) to let
            // it discover on its own than to skip enqueueing it here.
            OutboxMessage releaseHoldRow = dispatcher.Enqueue(
                new ReleaseHoldOutboxMessage(booking.HoldId), BookingsJsonSerializerContext.Default.ReleaseHoldOutboxMessage);
            OutboxMessage reverseTransactionRow = dispatcher.Enqueue(
                new ReverseTransactionOutboxMessage(booking.Id, refundAmount.Amount, refundAmount.Currency),
                BookingsJsonSerializerContext.Default.ReverseTransactionOutboxMessage);
            OutboxMessage reverseRedemptionRow = dispatcher.Enqueue(
                new ReverseRedemptionOutboxMessage(booking.Id), BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage);

            await dbContext.SaveChangesAsync(cancellationToken);

            await dispatcher.TryDispatchAsync(releaseHoldRow, cancellationToken);
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
            decimal? refundPercent = refundSnapshot.Amount == 0m
                ? null
                : refundSnapshot.RefundAmount / refundSnapshot.Amount * 100m;

            return BuildResponse(
                booking, refundSnapshot.RefundAmount, booking.TotalPrice.Currency, refundPercent, refundPending: false);
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
