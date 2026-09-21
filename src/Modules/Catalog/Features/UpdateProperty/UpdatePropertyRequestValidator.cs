using BuildingBlocks.Time;
using BuildingBlocks.Localization;
using FastEndpoints;
using FluentValidation;
using Microsoft.Extensions.Options;
namespace Catalog.Features.UpdateProperty;

public sealed class UpdatePropertyRequestValidator : Validator<UpdatePropertyRequest>
{
    // Injected rather than resolved from FastEndpoints' static service locator: DI builds
    // these, and so can a test - the rule needs to know which cultures this deployment serves, and
    // a validator that can only be constructed inside a host cannot be unit tested.
    public UpdatePropertyRequestValidator(IOptions<LocalizationSettings> localizationSettings)
    {
        LocalizationSettings localization = localizationSettings.Value;

        RuleFor(x => x.Name)
            .LocalizedText(localization, LocalizedTextRules.MaxNameLength);
        RuleFor(x => x.PropertyId).NotEmpty();
        RuleFor(x => x.PropertyType).IsInEnum();
        RuleFor(x => x.City).MaximumLength(100);

        // Rejected here rather than at the domain guard so the caller gets a
        // field-level 400. PropertyTimeZone.IsValid never throws; the throwing
        // FindSystemTimeZoneById would surface a bad id as a 500.
        RuleFor(x => x.TimeZoneId)
            .NotEmpty()
            .Must(PropertyTimeZone.IsValid)
            .WithMessage("'{PropertyValue}' is not a recognised IANA time zone identifier (for example 'Asia/Kuwait').");
    }
}
