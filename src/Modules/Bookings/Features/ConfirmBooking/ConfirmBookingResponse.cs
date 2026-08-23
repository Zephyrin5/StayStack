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
}
