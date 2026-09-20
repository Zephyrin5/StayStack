namespace Api.Security;

/// <summary>
///     Whether cookies this API sets carry the Secure flag. Bound from the
///     "Cookies" configuration section.
///     <para>
///         Configured rather than derived from <c>Request.IsHttps</c>, which is
///         correct only when <c>UseForwardedHeaders</c> has applied a trusted
///         proxy's X-Forwarded-Proto. A TLS-terminating proxy missing from
///         <c>ForwardedHeaders:KnownProxies</c> has its headers dropped, and the
///         refresh-token cookie would go out without Secure.
///     </para>
///     <para>
///         Defaults to true, so the failure is a cookie a browser refuses over plain HTTP - loud and
///         local - rather than a session token in the clear. Development and Testing turn it off in
///         their own appsettings files. Configuration rather than an IsDevelopment() check, so
///         production code is not deciding whether it is under test.
///     </para>
/// </summary>
public class CookieSecurityOptions
{
    public const string SectionName = "Cookies";

    public bool RequireSecure { get; set; } = true;

    /// <summary>
    ///     This API's own public origin (scheme + host), used at startup to
    ///     check the configured CORS origins against
    ///     <see cref="SameSite"/> - see SameSiteOriginCheck.
    ///     <para>
    ///         Declared rather than discovered, for the same reason
    ///         <see cref="RequireSecure"/> is. Behind a TLS-terminating proxy
    ///         the app's bound addresses are the proxy's, not the ones a
    ///         browser sees, so anything derived at runtime describes the wrong
    ///         side of the hop - which is precisely the mistake that shipped
    ///         refresh tokens without Secure.
    ///     </para>
    ///     <para>
    ///         Optional. Left empty, the startup check cannot run and says so
    ///         once, rather than guessing. It is a diagnostic, not a security
    ///         control: nothing in the request path reads it.
    ///     </para>
    /// </summary>
    public string? ApiOrigin { get; set; }

    /// <summary>
    ///     SameSite policy for cookies this API sets. Lax by default, which is
    ///     correct for every deployment where the SPA and the API share a
    ///     registrable domain - including a cross-origin one, since a site is
    ///     not an origin: port is not part of it, so localhost:3000 calling
    ///     localhost:5277 is cross-origin (hence CORS and AllowCredentials)
    ///     but same-site (hence Lax is sent). Lax also closes the classic CSRF
    ///     vector an attacker page auto-submitting a form POST would otherwise
    ///     reopen, so it is the right default to keep.
    ///     <para>
    ///         An SPA on a different registrable domain or scheme is genuinely cross-site, and a
    ///         browser attaches no Lax cookie to those requests: cookie auth then fails with no error
    ///         anywhere, refresh simply returning 401. Such a deployment sets None and takes on the
    ///         CSRF exposure Lax was preventing. None with <see cref="RequireSecure"/> false is
    ///         refused at startup - every browser rejects that pair, so it is a mistake, not a
    ///         deployment.
    ///     </para>
    /// </summary>
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;
}
