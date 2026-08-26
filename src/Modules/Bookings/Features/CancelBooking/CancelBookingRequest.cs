using Mediator;
namespace Bookings.Features.CancelBooking;

public record CancelBookingRequest : IRequest<CancelBookingResponse>
{
    public Guid BookingId { get; init; }

    // Only for a guest-checkout booking - see BookingAccessChecker. Never
    // read for an authenticated caller, whose CustomerId already proves
    // ownership.
    public string? ManagementToken { get; init; }
}
