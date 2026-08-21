using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.HoldAvailability;

public sealed class HoldAvailabilityRequestValidator : Validator<HoldAvailabilityRequest>
{
    public HoldAvailabilityRequestValidator()
    {
        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.CheckOut).GreaterThan(x => x.CheckIn);
        RuleFor(x => x.GuestCount).GreaterThan(0);

        // Deliberately NOT checking CheckIn against "today" or GuestCount
        // against a unit's MaxOccupancy here - both depend on data the
        // handler has to load anyway (the Unit itself), and duplicating
        // that check here would just mean two places that can drift out of
        // sync. This validator stays limited to pure request-shape rules;
        // the handler's guard clauses own everything that needs the Unit.
    }
}
