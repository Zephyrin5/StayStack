using Availability.Entities;
using System.Net;
using System.Net.Sockets;
namespace Api.Security;

/// <summary>
///     Derives the partition key that <c>HoldAvailabilityHandler</c>'s
///     concurrent-hold cap counts by. Unlike the hold-session cookie
///     (<see cref="HoldSessionCookie"/>), this is not client-supplied, so a
///     caller cannot mint themselves a fresh budget by discarding state -
///     which is the whole reason the cap moved onto it. See docs/adr/0016.
/// </summary>
public static class ClientNetworkKey
{
    /// <summary>
    ///     Sentinel for a request whose peer address the server couldn't
    ///     determine. Every such caller shares one budget, deliberately: an
    ///     unattributable request should not get a private allowance. Matches
    ///     what the "auth"/"holds" rate-limit partitions already do with a
    ///     null address.
    /// </summary>
    public const string Unknown = "unknown";

    /// <summary>
    ///     Longest value this returns is a full-form IPv6 /64
    ///     ("xxxx:xxxx:xxxx:xxxx::/64", 42 chars), which is what
    ///     <see cref="UnitAvailabilityHold.ClientKeyMaxLength"/> is sized for.
    /// </summary>
    public static string Resolve(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        // ::ffff:203.0.113.7 and 203.0.113.7 are the same peer reached over
        // different stacks. Normalised so one client can't hold two budgets
        // by which listener it happened to land on.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        // A single IPv6 customer is normally allocated a /64 (often a /56 or
        // /48), so keying on the full 128-bit address would make this cap
        // free to bypass - trivially more so than the cookie it replaces,
        // since an attacker there has 2^64 addresses rather than having to
        // re-request one. Masking to the /64 makes the budget belong to the
        // allocation rather than to whichever address inside it was used.
        //
        // NOTE: the "holds" and "auth" rate-limit partitions in Program.cs
        // still key on the full address and carry this gap. They bound
        // request rate rather than held inventory, so the exposure is much
        // smaller - but it is the same gap, and worth closing there too if
        // either is ever leaned on the way this cap now is.
        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);

        return $"{new IPAddress(bytes)}/64";
    }
}
