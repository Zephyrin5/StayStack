using BuildingBlocks.Localization;
using Microsoft.Extensions.Options;
namespace Api.Localization;

// Cross-field, so it cannot be a DataAnnotations attribute the way this
// type's other rules are: Required and MinLength each see one property, and
// this rule is about the relationship between two.
//
// IValidateOptions rather than a post-Build() throw in Program.cs, which is
// where the two existing cross-field checks live (BookingLifecyclePolicyOptions'
// deadline ordering, CookieSecurityOptions' SameSite/Secure pair). Those run
// after the host is built and throw InvalidOperationException; this runs
// inside the same ValidateOnStart pass that already validates this type's
// attributes, so one section's rules fail one way rather than two, and the
// rule travels with the registration instead of with a block of Program.cs
// that a second host would have to remember to copy.
internal sealed class LocalizationSettingsValidator : IValidateOptions<LocalizationSettings>
{
    public ValidateOptionsResult Validate(string? name, LocalizationSettings options)
    {
        // Only meaningful once both fields are individually valid. Left to
        // run on a blank DefaultCulture or an empty SupportedCultures, this
        // would report "the default culture isn't supported" on top of the
        // attribute failure that already says the real thing, and the second
        // message would send someone looking in the wrong place.
        if (string.IsNullOrWhiteSpace(options.DefaultCulture) || options.SupportedCultures.Length == 0)
        {
            return ValidateOptionsResult.Success;
        }

        // Ordinal-insensitive: culture names are case-insensitive to .NET, so
        // "EN" against ["en", "ar"] is a spelling difference, not a
        // misconfiguration. Exact match otherwise - no parent-culture
        // fallback, so a DefaultCulture of "en-US" needs "en-US" listed
        // rather than quietly resolving through "en". Accepting the parent
        // would mean SetDefaultCulture receives a culture the pipeline was
        // never told to support, which is the situation this check exists to
        // prevent.
        if (options.SupportedCultures.Contains(options.DefaultCulture, StringComparer.OrdinalIgnoreCase))
        {
            return ValidateOptionsResult.Success;
        }

        // A refusal rather than a warning, for the same reason the SameSite
        // check in Program.cs is: the result is a silently wrong product
        // rule, not a visible failure. SetDefaultCulture would name a culture
        // absent from AddSupportedCultures, so every request that doesn't
        // negotiate its own culture falls back to something the deployment
        // never declared - and the only symptom is content coming back in an
        // unexpected language.
        return ValidateOptionsResult.Fail(
            $"{LocalizationSettings.SectionName}:DefaultCulture ('{options.DefaultCulture}') is not one of " +
            $"SupportedCultures ([{string.Join(", ", options.SupportedCultures)}]). Culture negotiation would " +
            "default to a culture the request pipeline was never configured to support. Add it to " +
            "SupportedCultures, or set DefaultCulture to one of the cultures already listed.");
    }
}
