using Catalog.Features.CreateUnit;
using BuildingBlocks.Localization;
using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Options;
namespace Catalog.Features.UpdateUnit;

public sealed class UpdateUnitRequestValidator : Validator<UpdateUnitRequest>
{
    // Injected rather than resolved from FastEndpoints' static service locator: DI builds
    // these, and so can a test - the rule needs to know which cultures this deployment serves, and
    // a validator that can only be constructed inside a host cannot be unit tested.
    public UpdateUnitRequestValidator(IOptions<LocalizationSettings> localizationSettings)
    {
        LocalizationSettings localization = localizationSettings.Value;

        RuleFor(x => x.Name)
            .LocalizedText(localization, LocalizedTextRules.MaxNameLength);
        RuleFor(x => x.UnitId).NotEmpty();
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
