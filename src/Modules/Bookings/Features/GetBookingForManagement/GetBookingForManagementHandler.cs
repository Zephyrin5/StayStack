using Microsoft.Extensions.Options;
using Bookings.Entities;
using Bookings.Features.Common;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Time;
using Mediator;
using Bookings.Contracts;
namespace Bookings.Features.GetBookingForManagement;

public class GetBookingForManagementHandler(
    AppBookingsDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IBookingSessions bookingSessions,
    TimeProvider timeProvider,
    IOptions<BookingLifecyclePolicyOptions> policy) : IRequestHandler<GetBookingForManagementRequest, GetBookingForManagementResponse>
{
    public async ValueTask<GetBookingForManagementResponse> Handle(
        GetBookingForManagementRequest request, CancellationToken cancellationToken)
    {
        BookingAccess access = await BookingAccessChecker.ResolveAsync(
                              dbContext, request.BookingId, currentUserProvider.UserId,
                              await bookingSessions.GetSessionBookingIdAsync(cancellationToken), cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        Booking booking = access.Booking;

        DateOnly today = PropertyTimeZone.Today(timeProvider, booking.TimeZoneId);

        return new GetBookingForManagementResponse
        {
            BookingId = booking.Id,
            UnitId = booking.UnitId,
            BookingStatus = booking.BookingStatus,
            CheckIn = booking.CheckIn,
            CheckOut = booking.CheckOut,
            TotalPrice = booking.TotalPrice.Amount,
            Currency = booking.TotalPrice.Currency,
            // The same rule CancelBookingHandler enforces, from the same method
            // on the aggregate, so this never offers an action the API rejects.
            CanCancel = booking.CanBeCancelledOn(today),
            // Both bounds, matching the Reviews handlers exactly, so this never
            // offers a review the API rejects. The booking's own zone for the
            // same reason: resolved in different zones the two would disagree
            // on the checkout day itself.
            CanReview = booking.BookingStatus == BookingStatus.Confirmed
                        && booking.CheckOut <= today
                        && today <= booking.CheckOut.AddDays(policy.Value.ReviewWindowDaysAfterCheckOut)
        };
    }
}
