using FastEndpoints;
using FluentValidation;
namespace Bookings.Features.ConfirmBooking;

public sealed class ConfirmBookingRequestValidator : Validator<ConfirmBookingRequest>
{
    public ConfirmBookingRequestValidator()
    {
        RuleFor(x => x.HoldId).NotEmpty();
        RuleFor(x => x.GuestName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.GuestEmail).NotEmpty().EmailAddress().MaximumLength(200);
        RuleFor(x => x.GuestPhone).MaximumLength(50);
        RuleFor(x => x.GuestCount).GreaterThan(0);
    }
}
