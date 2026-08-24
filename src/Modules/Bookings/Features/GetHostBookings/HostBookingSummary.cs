using Bookings.Entities;
using SeedWork.Enums;
namespace Bookings.Features.GetHostBookings;

// A distinct shape from GetMyBookings' BookingSummary, not a reused one -
// GuestName/GuestEmail/GuestPhone are exactly what a host needs to know who
// is arriving and how to reach them, but have no reason to round-trip back
// to the customer who already knows their own contact details.
public record HostBookingSummary
{
    public Guid BookingId { get; init; }
    public Guid UnitId { get; init; }
    public Dictionary<string, string> UnitName { get; init; } = new Dictionary<string, string>();
    public string GuestName { get; init; } = string.Empty;
    public string GuestEmail { get; init; } = string.Empty;
    public string? GuestPhone { get; init; }
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public int GuestCount { get; init; }
    public decimal TotalPrice { get; init; }
    public Currency Currency { get; init; }
    public BookingStatus BookingStatus { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
