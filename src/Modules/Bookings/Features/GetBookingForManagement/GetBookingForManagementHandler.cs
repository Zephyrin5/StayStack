using Bookings.Entities;
using Bookings.Features.Common;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Time;
using Mediator;
namespace Bookings.Features.GetBookingForManagement;

public class GetBookingForManagementHandler(
    AppBookingsDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    TimeProvider timeProvider) : IRequestHandler<GetBookingForManagementRequest, GetBookingForManagementResponse>
{
    public async ValueTask<GetBookingForManagementResponse> Handle(
        GetBookingForManagementRequest request, CancellationToken cancellationToken)
    {
        Booking booking = await BookingAccessChecker.ResolveAsync(
                              dbContext, request.BookingId, currentUserProvider.UserId, request.ManagementToken, timeProvider, cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        return new GetBookingForManagementResponse
        {
            BookingId = booking.Id,
            UnitId = booking.UnitId,
            BookingStatus = booking.BookingStatus,
            CheckIn = booking.CheckIn,
            CheckOut = booking.CheckOut,
            TotalPrice = booking.TotalPrice.Amount,
            Currency = booking.TotalPrice.Currency,
            CanCancel = booking.BookingStatus != BookingStatus.Cancelled,
            // The booking's own zone, matching the Reviews handlers' inverted
            // guard exactly - resolved in different zones they would disagree
            // on the checkout day itself, offering a review the API rejects.
            CanReview = booking.BookingStatus == BookingStatus.Confirmed
                        && booking.CheckOut <= PropertyTimeZone.Today(timeProvider, booking.TimeZoneId)
        };
    }
}
