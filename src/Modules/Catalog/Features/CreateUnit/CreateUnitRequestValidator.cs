using BuildingBlocks.Localization;
using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
namespace Catalog.Features.CreateUnit;

public sealed class CreateUnitRequestValidator : Validator<CreateUnitRequest>
{
    // Injected rather than resolved from FastEndpoints' static service locator: DI builds
    // these, and so can a test - the rule needs to know which cultures this deployment serves, and
    // a validator that can only be constructed inside a host cannot be unit tested.
    public CreateUnitRequestValidator(IOptions<LocalizationSettings> localizationSettings)
    {
        LocalizationSettings localization = localizationSettings.Value;

        RuleFor(x => x.Name)
            .LocalizedText(localization, LocalizedTextRules.MaxNameLength);
        RuleFor(x => x.PropertyId).NotEmpty();
        RuleFor(x => x.MaxOccupancy).GreaterThan(0);
        RuleFor(x => x.BasePrice).GreaterThan(0);
        RuleFor(x => x.Currency).IsInEnum();

        // null is valid here (omitted entirely means "use the platform
        // default") - only a *provided* tier list needs to satisfy
        // CancellationPolicy.Create's own invariants. Reusing that factory
        // as the predicate rather than re-implementing its rules here
        // keeps there being exactly one place those rules are expressed.
        RuleFor(x => x.CancellationTiers)
            .Must(tiers => tiers is null || IsValidPolicy(tiers))
            .WithMessage(
                "Cancellation tiers must include exactly one zero-day floor tier, distinct non-negative " +
                "thresholds, refund percents between 0 and 100, and non-increasing percents as the " +
                "threshold gets closer to check-in.");

        // Whether PropertyId refers to a real Property is a database
        // concern, checked in the handler - Property lives in this same
        // module/DbContext, so no cross-module contract is needed for it
        // the way HostId needed one in CreateProperty.
    }

    internal static bool IsValidPolicy(IReadOnlyList<CancellationTier> tiers)
    {
        try
        {
            CancellationPolicy.Create(tiers);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
