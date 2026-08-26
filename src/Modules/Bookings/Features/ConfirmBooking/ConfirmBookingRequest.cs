using Mediator;
namespace Bookings.Features.ConfirmBooking;

public record ConfirmBookingRequest : IRequest<ConfirmBookingResponse>
{
    public Guid HoldId { get; init; }
    public required string GuestName { get; init; }
    public required string GuestEmail { get; init; }
    public string? GuestPhone { get; init; }

    // Optional - see ConfirmBookingHandler for how a redeemed code is
    // exclusive of the length-of-stay discount rather than stacking with
    // it. A rejection surfaces as a field-keyed ValidationException so the
    // client can show the specific reason against this field.
    public string? PromoCode { get; init; }
}
