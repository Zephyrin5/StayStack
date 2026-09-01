namespace Api.Security;

/// <summary>
///     Whether cookies this API sets carry the Secure flag. Bound from the
///     "Cookies" configuration section.
///     <para>
///         This used to be derived per request from
///         <c>HttpContext.Request.IsHttps</c>, which reads correctly only when
///         <c>UseForwardedHeaders</c> has actually applied the proxy's
///         X-Forwarded-Proto. That in turn requires the proxy to be trusted,
///         and <c>ForwardedHeaders:KnownProxies</c> ships empty - leaving only
///         the framework's loopback defaults. A TLS-terminating proxy at any
///         non-loopback address is therefore untrusted, its headers are
///         dropped, <c>IsHttps</c> is false, and the refresh-token cookie went
///         out without Secure. Deriving a security flag from transport the app
///         cannot see was the mistake; a deployment states it instead.
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
