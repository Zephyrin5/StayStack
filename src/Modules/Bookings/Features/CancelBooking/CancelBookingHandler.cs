using Bookings.Entities;
using Bookings.Features.Common;
using Bookings.Outbox;
using Bookings.Serialization;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Mediator;
using Outbox;
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
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        // Doesn't distinguish "doesn't exist" from "isn't yours" - same
        // reasoning as IHostAuthorization.RequireOwnership, now covering
        // two proof-of-ownership paths instead of one: a matching
        // CustomerId (authenticated) or a matching, not-yet-expired
        // management token (guest checkout) - see BookingAccessChecker's
        // own doc comment.
        Booking booking = await BookingAccessChecker.ResolveAsync(
                              dbContext, request.BookingId, currentUserProvider.UserId, request.ManagementToken, today, cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        // A booking confirmed before cancellation policies existed has no
        // snapshot - falls back to the same default new units get, rather
        // than fabricating a specific claim about what applied
        // retroactively. See Booking.CancellationPolicy's own doc comment.
        //
        // Computed unconditionally, not just on the fresh-cancel path below -
        // Cancel() only ever sets BookingStatus, never CancellationPolicy/
        // CheckIn/TotalPrice, so this is exactly as valid on an idempotent
        // recancel, and the response needs it either way (see RefundPending
        // below).
        CancellationPolicy cancellationPolicy = booking.CancellationPolicy ?? CancellationPolicy.CreateDefault();

        // Cancelling on or after check-in day itself lands on the same
        // strictest applicable tier as cancelling the moment check-in
        // starts, not an undefined negative day count.
        int daysBeforeCheckIn = Math.Max(booking.CheckIn.DayNumber - today.DayNumber, 0);
        decimal refundPercent = cancellationPolicy.ResolveRefundPercent(daysBeforeCheckIn);
        // The division has to happen first, in plain decimal, before the
        // one Money multiplication - see Money's own doc comment on why
        // `a * b / c` and `a * (b / c)` aren't the same value for a type
        // that rounds on every operation.
        Money refundAmount = booking.TotalPrice * (refundPercent / 100m);

        // Checked *before* dispatch, on both paths - the only way to know
        // deterministically whether there's real money behind this booking,
        // independent of whether ReverseTransactionAsync's own outbox
        // dispatch happens to land inline. On a recancel this also
        // correctly re-detects a still-Succeeded transaction left behind by
        // a first cancel's reversal that hasn't landed yet (slow retry, or
        // rarely dead-lettered) - GetRefundSnapshotAsync alone wouldn't see
        // that at all, since the refund sub-lifecycle never started.
        bool hasSucceededTransaction =
            await transactionReversal.GetSucceededTransactionAmountAsync(booking.Id, cancellationToken) is not null;

        // Idempotent re-cancel skips re-enqueueing the outbox messages a
        // second time - whatever was enqueued on the first Cancel() call is
        // already durable, and OutboxRelayJob keeps retrying it regardless
        // of how many times this endpoint is called. See docs/adr/0003.
        if (booking.BookingStatus != BookingStatus.Cancelled)
        {
            booking.Cancel();

            // Enqueued in the same SaveChangesAsync as booking.Cancel() -
            // atomic with the cancellation itself, then dispatched inline so
            // the common case still completes within this request. Each
            // message is independent: ReverseRedemptionAsync/
            // ReleaseHoldAsync are no-ops when there's nothing to reverse/
            // release, and ReverseTransactionAsync's own no-op case (nothing
            // Succeeded to reverse) is exactly what
            // hasSucceededTransaction above already determined - the
            // dispatch here still runs unconditionally regardless, since
            // it's just as safe (and simpler) to let it no-op on its own
            // than to skip enqueueing it.
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
        }

        if (!hasSucceededTransaction)
        {
            return new CancelBookingResponse
            {
                BookingId = booking.Id,
                BookingStatus = booking.BookingStatus,
                RefundAmount = null,
                Currency = null,
                RefundPercent = null,
                RefundPending = false
            };
        }

        // There was a Succeeded transaction as of the pre-dispatch check
        // above, so a refund is guaranteed to happen - idempotent, retried
        // by OutboxRelayJob/SweepDeadLetteredAsync until it does, regardless
        // of whether it's landed by the time this response is built.
        // RefundAmount/RefundPercent below are always the computed/requested
        // figures, not a read-back of the reversal's own eventual result
        // (which would be numerically identical once it lands anyway, since
        // it's the same refundAmount value passed into
        // ReverseTransactionAsync) - only RefundPending reflects whether
        // GetRefundSnapshotAsync can already confirm it landed.
        TransactionRefundSnapshot? refundSnapshot =
            await transactionReversal.GetRefundSnapshotAsync(booking.Id, cancellationToken);

        return new CancelBookingResponse
        {
            BookingId = booking.Id,
            BookingStatus = booking.BookingStatus,
            RefundAmount = refundAmount.Amount,
            Currency = refundAmount.Currency,
            RefundPercent = refundPercent,
            RefundPending = refundSnapshot is null
        };
    }
}
