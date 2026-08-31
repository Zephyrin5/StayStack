using FastEndpoints;
using FluentValidation;
namespace Availability.Features.HoldAvailability;

public sealed class HoldAvailabilityRequestValidator : Validator<HoldAvailabilityRequest>
{
    // A pure request-shape rule (doesn't need "today" or the Unit) - unlike
    // the lead-time cap, which does and lives in HoldAvailabilityHandler's
    // guard clauses instead. Without a bound here, an anonymous caller
    // could hold a single unit for up to a decade - this alone doesn't
    // stop that, but it bounds how much damage one hold can do.
    public const int MaxStayNights = 90;

    public HoldAvailabilityRequestValidator()
    {
        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.CheckOut).GreaterThan(x => x.CheckIn);
        RuleFor(x => x)
            .Must(x => x.CheckOut.DayNumber - x.CheckIn.DayNumber <= MaxStayNights)
            .WithName(nameof(HoldAvailabilityRequest.CheckOut))
            .WithMessage($"Stay length cannot exceed {MaxStayNights} nights.");
        RuleFor(x => x.GuestCount).GreaterThan(0);

        // Deliberately NOT checking CheckIn against "today" or GuestCount
        // against a unit's MaxOccupancy here - both depend on data the
        // handler has to load anyway (the Unit itself), and duplicating
        // that check here would just mean two places that can drift out of
        // sync. This validator stays limited to pure request-shape rules;
        // the handler's guard clauses own everything that needs the Unit.
    }
}
