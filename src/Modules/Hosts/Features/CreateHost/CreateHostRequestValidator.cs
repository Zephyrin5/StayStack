using BuildingBlocks.Localization;
using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Options;
namespace Hosts.Features.CreateHost;

public sealed class CreateHostRequestValidator : Validator<CreateHostRequest>
{
    // Injected rather than resolved from FastEndpoints' static service locator: DI builds
    // these, and so can a test - the rule needs to know which cultures this deployment serves, and
    // a validator that can only be constructed inside a host cannot be unit tested.
    public CreateHostRequestValidator(IOptions<LocalizationSettings> localizationSettings)
    {
        LocalizationSettings localization = localizationSettings.Value;

        RuleFor(x => x.DisplayName)
            .LocalizedText(localization, LocalizedTextRules.MaxDisplayNameLength)
            .When(x => x.DisplayName is not null);
        RuleFor(x => x.BusinessName).NotEmpty().MaximumLength(200);
        RuleFor(x => x.ContactEmail).NotEmpty().EmailAddress().MaximumLength(200);
        RuleFor(x => x.ContactPhone).MaximumLength(50);
    }
}
