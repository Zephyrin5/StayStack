using BuildingBlocks.Localization;
using System.ComponentModel.DataAnnotations;
namespace UnitTests.Localization;

// A unit test rather than a case in OptionsValidationTests, which is where the
// other "refuses to start" assertions live. Those layer an in-memory provider
// over appsettings.json, and configuration merges array elements by key - so
// an override can replace SupportedCultures:0 or append :2, but nothing can
// clear the ["en", "ar"] already declared there. The empty list is only
// reachable from a deployment whose configuration never declares the section
// at all, which is exactly the case worth rejecting and the one that harness
// cannot express. This asserts the same DataAnnotations pass that
// ValidateDataAnnotations/ValidateOnStart runs at boot.
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
        // paper over - it used to fall through to a hardcoded ["en", "ar"] in
        // ApiServicesRegistration, which meant appsettings could be cleared
        // without anything noticing.
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
