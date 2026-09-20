using Api.Security;
using BuildingBlocks.Configuration;
using Bookings.Contracts;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using System.Net;
namespace Api.Configuration;

/// <summary>
///     The cross-field rules that used to run as `if (...) throw` after Build(). As
///     <see cref="IValidateOptions{TOptions}"/> with ValidateOnStart they fail the same way, at the
///     same moment, beside the per-field rules generated from each type's DataAnnotations - and they
///     are reached by anything that resolves the options, not only by the one composition root.
/// </summary>
internal sealed class BookingLifecycleCrossFieldValidator : IValidateOptions<BookingLifecyclePolicyOptions>
{
    public ValidateOptionsResult Validate(string? name, BookingLifecyclePolicyOptions options) =>
        options.ManagementTokenLifetimeDaysAfterCheckOut >= options.ReviewWindowDaysAfterCheckOut
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                $"{BookingLifecyclePolicyOptions.SectionName}:ManagementTokenLifetimeDaysAfterCheckOut " +
                $"({options.ManagementTokenLifetimeDaysAfterCheckOut}) is shorter than " +
                $"ReviewWindowDaysAfterCheckOut ({options.ReviewWindowDaysAfterCheckOut}). Guest-checkout " +
                "callers would lose access to their booking before the review window closes, so the window " +
                "would apply only to signed-in customers - which is the asymmetry these settings exist to " +
                "remove. Raise the token lifetime to at least the review window.");
}

/// <summary>
///     Two ways cookie auth fails silently: a browser refusing the cookie, and a browser declining to
///     attach it. Both are configuration mistakes with no runtime symptom beyond "login does not stick".
/// </summary>
internal sealed class CookieSecurityCrossFieldValidator(
    IConfiguration configuration, IHostEnvironment environment, ILogger<CookieSecurityCrossFieldValidator> logger)
    : IValidateOptions<CookieSecurityOptions>
{
    public ValidateOptionsResult Validate(string? name, CookieSecurityOptions options)
    {
        // Browsers reject SameSite=None cookies that are not Secure, so cookie auth could never work.
        if (options.SameSite == SameSiteMode.None && !options.RequireSecure)
        {
            return ValidateOptionsResult.Fail(
                $"{CookieSecurityOptions.SectionName}:SameSite is None but RequireSecure is false. Browsers " +
                "reject SameSite=None cookies that are not Secure, so no session cookie would ever be stored. " +
                "A cross-site SPA needs both; a same-site one should leave SameSite at Lax.");
        }

        string[] corsOrigins = configuration.GetSection("App:Cors:AllowedOrigins").Get<string[]>() ?? [];

        if (corsOrigins.Length == 0 || options.SameSite != SameSiteMode.Lax)
        {
            return ValidateOptionsResult.Success;
        }

        if (string.IsNullOrWhiteSpace(options.ApiOrigin))
        {
            logger.LogWarning(
                "{Section}:ApiOrigin is not set, so the CORS/SameSite consistency check cannot run. If any " +
                "origin in Cors:AllowedOrigins is on a different registrable domain or scheme than this API, " +
                "the SameSite=Lax cookies it sets will never be attached to that origin's requests and " +
                "cookie-mode auth will fail with no visible error. Bearer tokens are unaffected.",
                CookieSecurityOptions.SectionName);

            return ValidateOptionsResult.Success;
        }

        string[] crossSite = [.. corsOrigins.Where(origin => !SameSiteOriginCheck.IsSameSite(origin, options.ApiOrigin))];

        // Allowed in Development, where the SPA and the API are routinely on different ports.
        if (crossSite.Length == 0 || environment.IsDevelopment())
        {
            return ValidateOptionsResult.Success;
        }

        return ValidateOptionsResult.Fail(
            $"{CookieSecurityOptions.SectionName}:SameSite is Lax, but these allowed CORS origins are " +
            $"cross-site with {CookieSecurityOptions.SectionName}:ApiOrigin ({options.ApiOrigin}): " +
            $"{string.Join(", ", crossSite)}. A browser will not attach a Lax cookie to their requests, so " +
            "cookie-mode auth cannot work for them and refresh would fail with no error visible anywhere. " +
            "Either serve the SPA same-site with the API, or leave those clients on bearer tokens (the " +
            "default - omit ?useCookies=true), or set SameSite to None with RequireSecure and accept that you " +
            "now own CSRF protection on every cookie-authenticated endpoint.");
    }
}

/// <summary>
///     Which proxies may set X-Forwarded-For/-Proto. Only loopback is trusted by default, so an
///     unlisted proxy means every caller shares one rate-limit partition and one hold cap - keyed on
///     the proxy's own address.
/// </summary>
public sealed class ForwardedHeadersSettings
{
    public const string SectionName = "ForwardedHeaders";

    public string[] KnownProxies { get; set; } = [];

    /// <summary>CIDR ranges, for a managed load balancer with no listable address.</summary>
    public string[] KnownNetworks { get; set; } = [];

    /// <summary>Declares that nothing sits in front of this app, which is the other honest answer.</summary>
    public bool ExposedDirectly { get; set; }
}

internal sealed class ForwardedHeadersSettingsValidator(IHostEnvironment environment)
    : IValidateOptions<ForwardedHeadersSettings>
{
    public ValidateOptionsResult Validate(string? name, ForwardedHeadersSettings options)
    {
        if (environment.IsDevelopment()
            || options.ExposedDirectly
            || options.KnownProxies.Length > 0
            || options.KnownNetworks.Length > 0)
        {
            return Parses(options);
        }

        return ValidateOptionsResult.Fail(
            $"App:{SectionName}:KnownProxies and :KnownNetworks are both empty outside Development. Only " +
            "loopback is trusted, so behind a proxy at any other address X-Forwarded-For/-Proto are dropped " +
            "and every caller shares one rate-limit partition keyed on the proxy's address - including the " +
            "anonymous read endpoints, which would serve 429s to every visitor at once. List the proxy " +
            $"addresses, or a CIDR range for a managed load balancer, or set App:{SectionName}:ExposedDirectly " +
            "to true if this app really does terminate its own TLS with nothing in front of it.");
    }

    // Parsed here rather than where the options are applied: a malformed address should fail startup
    // with the value in the message, not throw out of a configuration callback.
    private static ValidateOptionsResult Parses(ForwardedHeadersSettings options)
    {
        foreach (string proxy in options.KnownProxies)
        {
            if (!IPAddress.TryParse(proxy, out _))
            {
                return ValidateOptionsResult.Fail($"App:{SectionName}:KnownProxies contains '{proxy}', which is not an IP address.");
            }
        }

        foreach (string network in options.KnownNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(network, out _))
            {
                return ValidateOptionsResult.Fail($"App:{SectionName}:KnownNetworks contains '{network}', which is not a CIDR range.");
            }
        }

        return ValidateOptionsResult.Success;
    }

    private const string SectionName = ForwardedHeadersSettings.SectionName;
}

/// <summary>Turns the settings above into the options UseForwardedHeaders reads.</summary>
internal sealed class ConfigureForwardedHeaders(IOptions<ForwardedHeadersSettings> settings)
    : IConfigureOptions<ForwardedHeadersOptions>
{
    public void Configure(ForwardedHeadersOptions options)
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        foreach (string proxy in settings.Value.KnownProxies)
        {
            options.KnownProxies.Add(IPAddress.Parse(proxy));
        }

        foreach (string network in settings.Value.KnownNetworks)
        {
            options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }
    }
}

