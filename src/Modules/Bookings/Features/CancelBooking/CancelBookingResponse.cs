using Bookings.Entities;
namespace Bookings.Features.CancelBooking;

public record CancelBookingResponse
{
    public Guid BookingId { get; init; }
    public BookingStatus BookingStatus { get; init; }
}
