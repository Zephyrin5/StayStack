using Bookings.Entities;
using Bookings.Features.Common;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
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
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        Booking booking = await BookingAccessChecker.ResolveAsync(
                              dbContext, request.BookingId, currentUserProvider.UserId, request.ManagementToken, today, cancellationToken)
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
            CanReview = booking.BookingStatus == BookingStatus.Confirmed && booking.CheckOut <= today
        };
    }
}
