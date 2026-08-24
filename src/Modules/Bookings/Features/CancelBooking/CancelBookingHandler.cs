using Bookings.Entities;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Transactions.Contracts;
namespace Bookings.Features.CancelBooking;

public class CancelBookingHandler(
    AppBookingsDbContext dbContext,
    IHoldConfirmation holdConfirmation,
    ITransactionReversal transactionReversal,
    ICurrentUserProvider currentUserProvider) : IRequestHandler<CancelBookingRequest, CancelBookingResponse>
{
    public async ValueTask<CancelBookingResponse> Handle(CancelBookingRequest request, CancellationToken cancellationToken)
    {
        Booking booking = await dbContext.Bookings
                              .SingleOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        // Deliberately NotFoundException, not a 403, for a mismatched
        // CustomerId - same "doesn't exist and exists-but-isn't-yours must
        // look identical" reasoning as IHostAuthorization.RequireOwnership.
        // A null CustomerId (guest checkout) can never equal an
        // authenticated caller's UserId, so guest-checkout bookings
        // correctly never show up as cancellable here either - consistent
        // with GetMyBookings never listing them for anyone.
        if (booking.CustomerId != currentUserProvider.UserId)
        {
            throw new NotFoundException(nameof(Booking), request.BookingId);
        }

        booking.Cancel();
        await dbContext.SaveChangesAsync(cancellationToken);

        // Both released/resolved after the booking's own cancellation is
        // durable, not before - if either fails, the booking is still
        // correctly cancelled; the hold just sits 'booked' a bit longer
        // than ideal (cleaned up eventually by ExpiredHoldsSweepJob), and a
        // transaction stays wherever it was (resolvable later, or safe as
        // a known residual - see ITransactionReversal's own doc comment).
        // Nothing to compensate here, unlike ConfirmBookingHandler's
        // forward path: there's no second write whose failure could leave
        // the booking itself in a bad state.
        await holdConfirmation.ReleaseHoldAsync(booking.HoldId, cancellationToken);
        await transactionReversal.ReverseTransactionAsync(booking.Id, cancellationToken);

        return new CancelBookingResponse
        {
            BookingId = booking.Id,
            BookingStatus = booking.BookingStatus
        };
    }
}
