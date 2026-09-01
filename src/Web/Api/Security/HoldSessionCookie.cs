namespace Api.Security;

/// <summary>
///     An opaque per-browser correlator for anonymous hold requests - not a
///     security boundary (a scripted caller can drop the cookie and mint a
///     fresh one per request), so it's never used for authorization and no
///     longer enforces anything.
///     <para>
///         It used to carry HoldAvailabilityHandler's concurrent-hold cap,
///         which meant the cap was keyed on a value the caller chose:
///         discard the cookie, get a fresh budget. That cap now counts by
///         <see cref="ClientNetworkKey"/> instead. What's left here is the
///         one job this token can actually do - an ownership handle letting
///         an anonymous caller release or confirm their own hold. See
///         docs/adr/0016.
///     </para>
/// </summary>
public static class HoldSessionCookie
{
    private const string CookieName = "staystack_hold_session";

    extension(HttpRequest request)
    {
        /// <summary>
        ///     Returns the caller's existing hold-session token, or mints and
        ///     sets a new one on <paramref name="response"/> if none was
        ///     presented. Same Secure/SameSite reasoning as AuthCookies -
        ///     Secure declared by configuration rather than inferred from
        ///     Request.IsHttps (see <see cref="CookieSecurityOptions"/>), Lax
        ///     since this is never sent cross-site.
        /// </summary>
        public string GetOrCreateHoldSessionToken(
            HttpResponse response, CookieSecurityOptions cookieSecurity, TimeProvider timeProvider)
        {
            // Guid.TryParse, not just non-empty - the column this is
            // eventually written to is HasMaxLength(64), and nothing stops
            // a caller from sending an arbitrary (or arbitrarily long)
            // cookie value under this name. Trusting it verbatim would
            // surface as a raw Postgres 22001 (value too long) and a 500 on
            // the very next hold request, instead of just quietly minting a
            // fresh token the way an absent cookie already does.
            if (request.Cookies.TryGetValue(CookieName, out string? existing)
                && Guid.TryParse(existing, out _))
            {
                return existing;
            }

            string token = Guid.CreateVersion7().ToString();

            response.Cookies.Append(CookieName, token, new CookieOptions
            {
                HttpOnly = true,
                Secure = cookieSecurity.RequireSecure,
                SameSite = SameSiteMode.Lax,
                Expires = timeProvider.GetUtcNow().AddDays(1),
                Path = "/"
            });

            return token;
        }
    }
}
