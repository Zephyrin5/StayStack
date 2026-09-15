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
///         Defaults to true so the failure mode is a cookie a browser refuses
///         over plain HTTP - loud and local - rather than a session token
///         travelling in the clear. The two environments that genuinely serve
///         over HTTP turn it off in their own appsettings files: Development
///         (plain HTTP, avoiding dev-cert trust problems with Node's fetch)
///         and Testing (TestServer's in-memory transport is HTTP whatever the
///         environment is named). Configuration, not an IsDevelopment() check
///         in the request path, so production code isn't deciding whether it's
///         under test - and so a real HTTP-only deployment is a config choice
///         someone has to make deliberately.
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
    ///         A deployment that puts the SPA on a *different* registrable
    ///         domain from the API - or on a different scheme, under schemeful
    ///         same-site - is genuinely cross-site, and a browser will not
    ///         attach a Lax cookie to those requests at all. Cookie auth then
    ///         fails with no error anywhere: the cookie is simply never sent
    ///         and refresh returns 401. That deployment must set this to None,
    ///         and accept that None is a CSRF exposure Lax was preventing.
    ///     </para>
    ///     <para>
    ///         This is configuration rather than a fixed value because the
    ///         answer depends on a topology the code cannot see: it lives in
    ///         Cors:AllowedOrigins and in whatever hostname the API is served
    ///         under. Making it a setting is what turns "silently broken" into
    ///         a decision someone records. None with
    ///         <see cref="RequireSecure"/> false is rejected at startup - every
    ///         browser refuses that combination, so it is never a deployment,
    ///         only a mistake.
    ///     </para>
    /// </summary>
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Lax;
}
