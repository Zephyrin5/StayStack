using Catalog.Contracts;
using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Options;
namespace Bookings.Features.HoldAvailability;

public sealed class HoldAvailabilityRequestValidator : Validator<HoldAvailabilityRequest>
{
    // A pure request-shape rule (doesn't need "today" or the Unit) - unlike
    // the lead-time cap, which does and lives in HoldAvailabilityHandler's
    // guard clauses instead. It bounds how much of a unit one hold can block.
    // The number comes from StaySearchPolicyOptions, because GetProperties
    // must apply the same one or search and hold disagree about what is
    // bookable.
    public HoldAvailabilityRequestValidator(IOptions<StaySearchPolicyOptions> staySearchPolicy)
    {
        int maxStayNights = staySearchPolicy.Value.MaxStayNights;

        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.CheckOut).GreaterThan(x => x.CheckIn);
        RuleFor(x => x)
            .Must(x => x.CheckOut.DayNumber - x.CheckIn.DayNumber <= maxStayNights)
            .WithName(nameof(HoldAvailabilityRequest.CheckOut))
            .WithMessage($"Stay length cannot exceed {maxStayNights} nights.");
        RuleFor(x => x.GuestCount).GreaterThan(0);

        // Deliberately NOT checking CheckIn against "today" or GuestCount
        // against a unit's MaxOccupancy here - both depend on data the
        // handler has to load anyway (the Unit itself), and duplicating
        // that check here would just mean two places that can drift out of
        // sync. This validator stays limited to pure request-shape rules;
        // the handler's guard clauses own everything that needs the Unit.
    }
}
