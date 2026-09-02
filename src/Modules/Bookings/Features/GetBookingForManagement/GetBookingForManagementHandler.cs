using Microsoft.Extensions.Options;
using BuildingBlocks.Policies;
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
    TimeProvider timeProvider,
    IOptions<BookingLifecyclePolicyOptions> policy) : IRequestHandler<GetBookingForManagementRequest, GetBookingForManagementResponse>
{
    public async ValueTask<GetBookingForManagementResponse> Handle(
        GetBookingForManagementRequest request, CancellationToken cancellationToken)
    {
        Booking booking = await BookingAccessChecker.ResolveAsync(
                              dbContext, request.BookingId, currentUserProvider.UserId, request.ManagementToken, timeProvider,
            policy.Value.ManagementTokenLifetimeDaysAfterCheckOut, cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), request.BookingId);

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
            CanCancel = booking.BookingStatus != BookingStatus.Cancelled,
            // Both bounds, matching the Reviews handlers exactly - offering a
            // review the API would then reject is the failure this flag
            // exists to avoid, and an upper bound on only one side would
            // reintroduce it at the far end. The booking's own zone for the
            // same reason: resolved in different zones the two would disagree
            // on the checkout day itself.
            CanReview = booking.BookingStatus == BookingStatus.Confirmed
                        && booking.CheckOut <= today
                        && today <= booking.CheckOut.AddDays(policy.Value.ReviewWindowDaysAfterCheckOut)
        };
    }
}
