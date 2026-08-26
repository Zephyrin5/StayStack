using Bookings.Entities;
using Bookings.Features.Common;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Mediator;
using Transactions.Contracts;
namespace Bookings.Features.CancelBooking;

public class CancelBookingHandler(
    AppBookingsDbContext dbContext,
    IHoldConfirmation holdConfirmation,
    IPromotionRedemption promotionRedemption,
    ITransactionReversal transactionReversal,
    ICurrentUserProvider currentUserProvider) : IRequestHandler<CancelBookingRequest, CancelBookingResponse>
{
    public async ValueTask<CancelBookingResponse> Handle(CancelBookingRequest request, CancellationToken cancellationToken)
    {
        // Doesn't distinguish "doesn't exist" from "isn't yours" - same
        // reasoning as IHostAuthorization.RequireOwnership, now covering
        // two proof-of-ownership paths instead of one: a matching
        // CustomerId (authenticated) or a matching management token
        // (guest checkout) - see BookingAccessChecker's own doc comment.
        Booking booking = await BookingAccessChecker.ResolveAsync(
                              dbContext, request.BookingId, currentUserProvider.UserId, request.ManagementToken, cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        booking.Cancel();
        await dbContext.SaveChangesAsync(cancellationToken);

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
        await holdConfirmation.ReleaseHoldAsync(booking.HoldId, cancellationToken);
        await transactionReversal.ReverseTransactionAsync(booking.Id, cancellationToken);
        await promotionRedemption.ReverseRedemptionAsync(booking.Id, cancellationToken);

        return new CancelBookingResponse
        {
            BookingId = booking.Id,
            BookingStatus = booking.BookingStatus
        };
    }
}
