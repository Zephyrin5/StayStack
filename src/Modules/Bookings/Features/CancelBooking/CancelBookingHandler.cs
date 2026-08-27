using Bookings.Entities;
using Bookings.Features.Common;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Mediator;
using SeedWork.ValueObjects;
using Transactions.Contracts;
namespace Bookings.Features.CancelBooking;

public class CancelBookingHandler(
    AppBookingsDbContext dbContext,
    IHoldConfirmation holdConfirmation,
    IPromotionRedemption promotionRedemption,
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

        booking.Cancel();
        await dbContext.SaveChangesAsync(cancellationToken);

        // A booking confirmed before cancellation policies existed has no
        // snapshot - falls back to the same default new units get, rather
        // than fabricating a specific claim about what applied
        // retroactively. See Booking.CancellationPolicy's own doc comment.
        CancellationPolicy cancellationPolicy = booking.CancellationPolicy ?? CancellationPolicy.CreateDefault();

        // Cancelling on or after check-in day itself lands on the same
        // strictest applicable tier as cancelling the moment check-in
        // starts, not an undefined negative day count.
        int daysBeforeCheckIn = Math.Max(booking.CheckIn.DayNumber - today.DayNumber, 0);
        decimal refundPercent = cancellationPolicy.ResolveRefundPercent(daysBeforeCheckIn);
        decimal refundAmount = booking.TotalPrice * refundPercent / 100m;

        // Released/resolved/reversed after the booking's own cancellation is
        // durable, not before - if any fails, the booking is still
        // correctly cancelled; the hold just sits 'booked' a bit longer
        // than ideal (cleaned up eventually by ExpiredHoldsSweepJob), a
        // transaction stays wherever it was (resolvable later, or safe as
        // a known residual - see ITransactionReversal's own doc comment),
        // and a redeemed code stays consumed a bit longer than ideal.
        // Nothing to compensate here, unlike ConfirmBookingHandler's
        // forward path: there's no second write whose failure could leave
        // the booking itself in a bad state. ReverseRedemptionAsync is a
        // no-op if this booking never redeemed a code.
        //
        // Each of the three runs independently of the others' outcome - a
        // failed refund reversal must not prevent the hold release or
        // promotion reversal from even being attempted, and vice versa.
        // Same failure-isolation idiom ConfirmBookingHandler's own
        // compensation block uses (see its doc comment there).
        List<Exception> compensationFailures = [];
        decimal? actualRefundAmount = null;

        try
        {
            await holdConfirmation.ReleaseHoldAsync(booking.HoldId, cancellationToken);
        }
        catch (Exception releaseException)
        {
            compensationFailures.Add(releaseException);
        }

        try
        {
            actualRefundAmount = await transactionReversal.ReverseTransactionAsync(booking.Id, refundAmount, cancellationToken);
        }
        catch (Exception reverseTransactionException)
        {
            compensationFailures.Add(reverseTransactionException);
        }

        try
        {
            await promotionRedemption.ReverseRedemptionAsync(booking.Id, cancellationToken);
        }
        catch (Exception reverseRedemptionException)
        {
            compensationFailures.Add(reverseRedemptionException);
        }

        if (compensationFailures.Count > 0)
        {
            throw new AggregateException(
                "Booking was cancelled, but one or more post-cancellation cleanup actions failed.",
                compensationFailures);
        }

        return new CancelBookingResponse
        {
            BookingId = booking.Id,
            BookingStatus = booking.BookingStatus,
            RefundAmount = actualRefundAmount,
            // Null in lockstep with RefundAmount - a policy percent with no
            // actual money behind it (nothing was ever Succeeded) would be
            // misleading to show as if a refund is happening.
            RefundPercent = actualRefundAmount is not null ? refundPercent : null
        };
    }
}
