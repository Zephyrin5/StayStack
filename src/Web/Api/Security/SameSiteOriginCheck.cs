using System.Net;
namespace Api.Security;

/// <summary>
///     Decides whether the configured CORS origins can actually receive the
///     cookies this API sets - the one CORS/SameSite interaction that fails
///     silently.
///     <para>
///         The two settings answer different questions and only one of them is
///         about origins. CORS grants an origin permission to read a response;
///         SameSite decides whether the browser attaches the cookie in the
///         first place, comparing <em>sites</em> (registrable domain plus
///         scheme), not origins. So a deployment can allow an origin through
///         CORS, watch the preflight succeed, and still never see the cookie -
///         with no error anywhere. The symptom reported is "refresh randomly
///         doesn't work in the browser".
///     </para>
/// </summary>
public static class SameSiteOriginCheck
{
    /// <summary>
    ///     Whether <paramref name="origin"/> is same-site with
    ///     <paramref name="apiOrigin"/> for SameSite purposes.
    ///     <para>
    ///         Schemeful same-site: http and https are different sites to a
    ///         modern browser even on identical hosts. Port is ignored, which
    ///         is why localhost:3000 and localhost:5277 are same-site in dev
    ///         despite being different origins - the case that makes
    ///         AllowCredentials necessary and SameSite=Lax still correct.
    ///     </para>
    /// </summary>
    public static bool IsSameSite(string origin, string apiOrigin)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out Uri? a)
            || !Uri.TryCreate(apiOrigin, UriKind.Absolute, out Uri? b))
        {
            // Unparseable configuration is not this check's to report - the
            // CORS registration will fail on it more clearly. Treated as
            // same-site so this never becomes the error someone sees first.
            return true;
        }

        return string.Equals(a.Scheme, b.Scheme, StringComparison.OrdinalIgnoreCase)
               && string.Equals(RegistrableDomain(a.Host), RegistrableDomain(b.Host), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     The registrable domain, approximated as the last two labels.
    ///     <para>
    ///         Deliberately approximate, and biased toward <em>not</em>
    ///         failing a startup. A real implementation needs the Public
    ///         Suffix List - "a.co.uk" and "b.co.uk" are different sites but
    ///         share their last two labels, so this call says same-site and
    ///         the check stays quiet. That is the right way to be wrong here:
    ///         a missed warning leaves a deployment exactly where it is today,
    ///         while a false positive refuses to start a correct one. Pulling
    ///         in a PSL dependency to sharpen a startup hint is not a trade
    ///         worth making.
    ///     </para>
    /// </summary>
    private static string RegistrableDomain(string host)
    {
        if (IPAddress.TryParse(host, out _))
        {
            return host;
        }

        string[] labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries);

        return labels.Length <= 2 ? host : string.Join('.', labels[^2..]);
    }
}
