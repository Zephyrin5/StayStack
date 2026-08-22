using Mediator;
namespace Bookings.Features.ConfirmBooking;

public record ConfirmBookingRequest : IRequest<ConfirmBookingResponse>
{
    public Guid HoldId { get; init; }
    public required string GuestName { get; init; }
    public required string GuestEmail { get; init; }
    public string? GuestPhone { get; init; }
}
