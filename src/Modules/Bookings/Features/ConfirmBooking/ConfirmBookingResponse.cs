using Bookings.Entities;
using SeedWork.Enums;
namespace Bookings.Features.ConfirmBooking;

public record ConfirmBookingResponse
{
    public Guid BookingId { get; init; }
    public BookingStatus BookingStatus { get; init; }
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public decimal TotalPrice { get; init; }
    public Currency Currency { get; init; }

    // Set only for a guest-checkout booking (no authenticated account) -
    // the raw booking-management token, returned exactly this once. Lets
    // the client build a "manage your booking" link (view/cancel, and once
    // eligible, leave a review) with no account required. Null for an
    // authenticated caller, whose own session already proves ownership.
    public string? ManagementToken { get; init; }
}
