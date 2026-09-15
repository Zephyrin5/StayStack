using BuildingBlocks.Localization;
using System.ComponentModel.DataAnnotations;
namespace UnitTests.Localization;

// Pins the attributes themselves, without a host. It is not the whole story:
// OptionsValidationTests covers the same rules through a real ValidateOnStart,
// which is what proves they are actually wired rather than merely declared.
//
// Worth knowing if you extend either file - the two harnesses reach this type
// differently, and only one of them can produce an empty SupportedCultures.
// OptionsValidationTests' AssertRefusesToStart layers an in-memory provider
// over appsettings.json, and configuration merges array elements by key, so an
// override can replace SupportedCultures:0 or append :2 but nothing can clear
// the ["en", "ar"] already declared there. Reaching the empty list needs the
// Configure<T> DI override instead, which replaces the bound value outright.
public class LocalizationSettingsValidationTests
{
    private static IReadOnlyList<ValidationResult> Validate(LocalizationSettings settings)
    {
        List<ValidationResult> results = [];
        Validator.TryValidateObject(settings, new ValidationContext(settings), results, validateAllProperties: true);
        return results;
    }

    [Fact]
    public void AnEmptySupportedCulturesList_FailsValidation()
    {
        // The bilingual requirement is a product guarantee, so "no languages
        // configured" is a misconfiguration to refuse at boot, not a state to
        // paper over with hardcoded cultures that would hide appsettings being
        // cleared.
        LocalizationSettings settings = new LocalizationSettings
        {
            DefaultCulture = "en",
            SupportedCultures = []
        };

        IReadOnlyList<ValidationResult> results = Validate(settings);

        Assert.Contains(results, r => r.MemberNames.Contains(nameof(LocalizationSettings.SupportedCultures)));
    }

    [Fact]
    public void AConfiguredSupportedCulturesList_PassesValidation()
    {
        // The other half - validation must not reject a list merely because
        // it isn't the en/ar pair appsettings happens to ship.
        LocalizationSettings settings = new LocalizationSettings
        {
            DefaultCulture = "ar",
            SupportedCultures = ["ar", "fr"]
        };

        IReadOnlyList<ValidationResult> results = Validate(settings);

        Assert.Empty(results);
    }
}
