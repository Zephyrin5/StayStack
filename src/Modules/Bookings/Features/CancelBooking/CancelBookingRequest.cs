using Mediator;
namespace Bookings.Features.CancelBooking;

public record CancelBookingRequest : IRequest<CancelBookingResponse>
{
    public Guid BookingId { get; init; }
}
