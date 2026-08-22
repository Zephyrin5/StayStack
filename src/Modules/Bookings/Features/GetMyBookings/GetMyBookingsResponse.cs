using Bookings.Entities;
namespace Bookings.Features.GetMyBookings;

public record GetMyBookingsResponse
{
    public List<BookingSummary> Bookings { get; init; } = [];
}

public record BookingSummary
{
    public Guid BookingId { get; init; }
    public Guid UnitId { get; init; }
    public Dictionary<string, string> UnitName { get; init; } = new Dictionary<string, string>();
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public int GuestCount { get; init; }
    public decimal TotalPrice { get; init; }
    public string Currency { get; init; } = string.Empty;
    public BookingStatus BookingStatus { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
