using Bookings.Entities;
using SeedWork.Enums;
namespace Bookings.Features.GetMyBookings;

public record BookingSummary
{
    public Guid BookingId { get; init; }
    public Guid UnitId { get; init; }
    public Dictionary<string, string> UnitName { get; init; } = new Dictionary<string, string>();
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public int GuestCount { get; init; }
    public decimal TotalPrice { get; init; }
    public Currency Currency { get; init; }
    public BookingStatus BookingStatus { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
}
