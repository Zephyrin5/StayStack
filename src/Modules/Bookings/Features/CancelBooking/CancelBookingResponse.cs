using Bookings.Entities;
namespace Bookings.Features.CancelBooking;

public record CancelBookingResponse
{
    public Guid BookingId { get; init; }
    public BookingStatus BookingStatus { get; init; }

    // Null when there was nothing Succeeded to refund (no payment had
    // cleared yet, or there was never a transaction at all) - not the same
    // as a real 0% tier, which still returns 0m here, not null.
    public decimal? RefundAmount { get; init; }
    public decimal? RefundPercent { get; init; }
}
