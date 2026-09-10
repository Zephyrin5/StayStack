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
        RuleFor(x => x.PromoCode).MaximumLength(30);


        // No rule for IdempotencyKey here, deliberately - see that property's
        // own comment. It is assigned by the endpoint after binding, which is
        // after this runs, so a rule here would never see a real value.
    }
}
