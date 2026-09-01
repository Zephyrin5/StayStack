using Identity.Configurations;
using Microsoft.Extensions.Primitives;
namespace Api.Security;

/// <summary>
///     Cookie-mode auth support - opt-in via `?useCookies=true` on the
///     token-minting endpoints (sign-in, register, refresh-token,
///     become-host). When requested, the refresh token goes into an
///     httpOnly cookie instead of the JSON response body; token mode
///     (the default, no query flag) is unchanged and is what mobile/
///     non-browser clients keep using. See CookieAuthTests.cs and the
///     endpoints themselves for how the two modes are chosen per request.
/// </summary>
public static class AuthCookies
{
    private const string RefreshTokenCookieName = "staystack_refresh_token";

    extension(HttpResponse response)
    {
        public void SetRefreshTokenCookie(string refreshToken,
            AuthTokenConfiguration tokenSettings,
            CookieSecurityOptions cookieSecurity,
            TimeProvider timeProvider)
        {
            response.Cookies.Append(RefreshTokenCookieName, refreshToken, new CookieOptions
            {
                HttpOnly = true,
                // Declared by configuration, not derived from
                // Request.IsHttps. IsHttps only reports the proxy's scheme
                // once UseForwardedHeaders has trusted that proxy, and
                // ForwardedHeaders:KnownProxies ships empty - so behind a
                // TLS-terminating proxy on any non-loopback address this
                // silently evaluated false and shipped the refresh token
                // without Secure. See CookieSecurityOptions for why the
                // default is true and which environments opt out.
                Secure = cookieSecurity.RequireSecure,
                // Lax by default: this cookie is only ever attached to
                // same-site JS-initiated fetch calls, never a cross-site
                // top-level navigation - and SameSite compares registrable
                // domain, not port, so localhost:3000/5277 already count as
                // same-site in dev even though they are different origins
                // (which is why CORS still needs AllowCredentials). Lax also
                // closes the classic CSRF vector (an attacker page
                // auto-submitting a form POST here) a cookie-based credential
                // would otherwise reopen.
                //
                // Configurable because a deployment that splits the SPA and
                // API across registrable domains is genuinely cross-site, and
                // a Lax cookie is then never sent at all - see
                // CookieSecurityOptions.SameSite.
                SameSite = cookieSecurity.SameSite,
                Expires = timeProvider.GetUtcNow().AddDays(tokenSettings.RefreshTokenLifespanInDays),
                Path = "/"
            });
        }
        public void DeleteRefreshTokenCookie()
        {
            response.Cookies.Delete(RefreshTokenCookieName, new CookieOptions { Path = "/" });
        }
    }

    extension(HttpRequest request)
    {
        public string? GetRefreshTokenFromCookie()
        {
            return request.Cookies.TryGetValue(RefreshTokenCookieName, out string? value) ? value : null;
        }
        /// <summary>
        ///     ?useCookies=true opts a caller into cookie mode - matches
        ///     ASP.NET Core Identity's own MapIdentityApi precedent for this exact
        ///     web-vs-mobile split. Absent/false keeps today's token-mode behavior
        ///     unchanged, which mobile/non-browser clients depend on.
        /// </summary>
        public bool WantsCookieAuth()
        {
            return request.Query.TryGetValue("useCookies", out StringValues value)
                   && bool.TryParse(value, out bool useCookies)
                   && useCookies;
        }
    }
}
