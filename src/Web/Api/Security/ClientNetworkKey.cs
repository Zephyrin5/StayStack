using Bookings.Entities;
using System.Net;
using System.Net.Sockets;
namespace Api.Security;

/// <summary>
///     The partition key <c>HoldAvailabilityHandler</c>'s concurrent-hold cap counts by. Derived from
///     the peer address, never client-supplied, so a caller cannot mint a fresh budget by discarding
///     state (docs/adr/0016).
/// </summary>
public static class ClientNetworkKey
{
    /// <summary>
    ///     A request with no determinable peer address. All of them share one budget: an unattributable
    ///     request should not get a private allowance.
    /// </summary>
    public const string Unknown = "unknown";

/// <summary>
    ///     Longest value is a full-form IPv6 /64 (42 characters), which
    ///     <see cref="UnitAvailabilityHold.ClientKeyMaxLength"/> is sized for.
    /// </summary>
    public static string Resolve(IPAddress? address)
    {
        if (address is null)
        {
            return Unknown;
        }

        // The same peer over different stacks, so one client cannot hold two budgets.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        // A single IPv6 customer is normally allocated a /64 (often a /56 or
        // /48), so keying on the full 128-bit address would give an attacker
        // 2^64 budgets. Masking to the /64 makes the budget belong to the
        // allocation rather than to whichever address inside it was used.
        //
        // NOTE: the "holds" and "auth" rate-limit partitions in Program.cs
        // key on the full address and have this gap. They bound request rate
        // rather than held inventory, so the exposure is much smaller - but it
        // is worth closing there too if either is ever leaned on the way this
        // cap is.
        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);

        return $"{new IPAddress(bytes)}/64";
    }
}
