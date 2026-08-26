using System.Security.Cryptography;
using System.Text;
namespace BuildingBlocks.Security;

/// <summary>
///     The generate-a-random-opaque-token-and-hash-it primitive shared by
///     every "issue a bearer credential, persist only its hash, look it up
///     by re-hashing an incoming value" flow in this app - originally
///     inlined in Identity's AuthTokenProvider (refresh tokens) before
///     Bookings' guest booking-management token needed the exact same two
///     operations. Deliberately just these two operations, not a "token
///     service" - what each caller does around them (rotation/family/reuse
///     detection for refresh tokens vs. a single long-lived reusable token
///     for booking management) differs enough that forcing it into one
///     shared abstraction would need to be configurable for behaviors most
///     callers don't want, rather than actually shared.
/// </summary>
public static class SecureToken
{
    public static string Generate()
    {
        byte[] randomNumber = new byte[64];
        using RandomNumberGenerator rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Convert.ToBase64String(randomNumber);
    }

    public static string Hash(string token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(token);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
