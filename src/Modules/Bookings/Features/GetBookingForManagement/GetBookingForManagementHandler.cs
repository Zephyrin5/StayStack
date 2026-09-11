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
                              dbContext, request.BookingId, currentUserProvider.UserId, request.ManagementToken,
                              await bookingSessions.GetSessionBookingIdAsync(cancellationToken), timeProvider,
            policy.Value.ManagementTokenLifetimeDaysAfterCheckOut, cancellationToken)
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
            // The same rule CancelBookingHandler enforces, from the same
            // method on the aggregate. This used to ignore dates entirely and
            // advertise CanCancel for a stay that had already happened -
            // offering an action the API would reject, which is precisely the
            // failure the CanReview flag below was written to avoid.
            CanCancel = booking.CanBeCancelledOn(today),
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
