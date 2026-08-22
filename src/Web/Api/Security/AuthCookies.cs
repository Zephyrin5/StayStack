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
            AuthTokenConfiguration tokenSettings)
        {
            response.Cookies.Append(RefreshTokenCookieName, refreshToken, new CookieOptions
            {
                HttpOnly = true,
                // Tied to the actual request scheme, not an environment name
                // check (e.g. IsDevelopment()) - dev runs over plain HTTP (see
                // the rest of this session's notes on why - avoiding ASP.NET
                // Core dev-cert trust issues with Node's fetch), and the
                // integration test suite's TestServer transport is HTTP too
                // regardless of environment name ("Testing", not
                // "Development"). A Secure cookie set on a non-HTTPS response
                // is correctly refused by any real cookie jar (browser or
                // HttpClient), so this has to track the actual scheme, not
                // guess at it from the environment - confirmed by
                // CookieAuthTests.cs failing against a naive
                // !environment.IsDevelopment() check.
                Secure = response.HttpContext.Request.IsHttps,
                // Lax, not None: this cookie is only ever attached to same-site
                // JS-initiated fetch calls, never a cross-site top-level
                // navigation - and SameSite compares registrable domain, not
                // port, so localhost:3000 (frontend) and localhost:5277 (API)
                // already count as same-site in dev. Lax also closes the
                // classic CSRF vector (an attacker page auto-submitting a form
                // POST here) that a cookie-based credential would otherwise
                // reopen.
                SameSite = SameSiteMode.Lax,
                Expires = DateTimeOffset.UtcNow.AddDays(tokenSettings.RefreshTokenLifespanInDays),
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
