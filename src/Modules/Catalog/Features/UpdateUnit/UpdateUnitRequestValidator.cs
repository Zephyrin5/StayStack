using Catalog.Features.CreateUnit;
using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.UpdateUnit;

public sealed class UpdateUnitRequestValidator : Validator<UpdateUnitRequest>
{
    public UpdateUnitRequestValidator()
    {
        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().WithMessage("At least one localized name value is required.");
        RuleFor(x => x.MaxOccupancy).GreaterThan(0);
        RuleFor(x => x.BasePrice).GreaterThan(0);
        RuleFor(x => x.Currency).IsInEnum();

        // Required here, unlike CreateUnitRequest - see UpdateUnitRequest's
        // own doc comment. Reuses CreateUnitRequestValidator's predicate
        // rather than duplicating CancellationPolicy.Create's rules a
        // third time.
        RuleFor(x => x.CancellationTiers)
            .NotEmpty()
            .Must(CreateUnitRequestValidator.IsValidPolicy)
            .WithMessage(
                "Cancellation tiers must include exactly one zero-day floor tier, distinct non-negative " +
                "thresholds, refund percents between 0 and 100, and non-increasing percents as the " +
                "threshold gets closer to check-in.");
    }
}
