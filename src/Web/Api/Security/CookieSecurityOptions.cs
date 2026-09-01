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
}
