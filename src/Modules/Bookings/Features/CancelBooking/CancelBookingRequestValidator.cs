using FastEndpoints;
using FluentValidation;
namespace Bookings.Features.CancelBooking;

public sealed class CancelBookingRequestValidator : Validator<CancelBookingRequest>
{
    public CancelBookingRequestValidator()
    {
        RuleFor(x => x.BookingId).NotEmpty();
    }
}
