using FastEndpoints;
using FluentValidation;
namespace Bookings.Features.CreateBookingSession;

public sealed class CreateBookingSessionRequestValidator : Validator<CreateBookingSessionRequest>
{
    public CreateBookingSessionRequestValidator()
    {
        RuleFor(x => x.BookingId).NotEmpty();
        RuleFor(x => x.ManagementToken).NotEmpty();
    }
}
