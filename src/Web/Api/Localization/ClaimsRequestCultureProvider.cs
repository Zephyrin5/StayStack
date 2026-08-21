using Microsoft.AspNetCore.Localization;
namespace Api.Localization;

/// <summary>
///     Slots into RequestLocalizationOptions.RequestCultureProviders. A
///     signed-in Customer's stored preference (Customer.PreferredLanguage,
///     carried into the JWT as a "preferred_language" claim - see
///     AuthTokenProvider) should outrank Accept-Language negotiation, which
///     only reflects what the browser sends, not what the person actually
///     chose. Returns null for anonymous/guest-checkout requests, letting
///     the next provider in the chain (Accept-Language) take over.
/// </summary>
public class ClaimsRequestCultureProvider : RequestCultureProvider
{
    public override Task<ProviderCultureResult?> DetermineProviderCultureResult(HttpContext httpContext)
    {
        string? languageClaim = httpContext.User.FindFirst("preferred_language")?.Value;

        return Task.FromResult(
            string.IsNullOrWhiteSpace(languageClaim) ? null : new ProviderCultureResult(languageClaim));
    }
}
