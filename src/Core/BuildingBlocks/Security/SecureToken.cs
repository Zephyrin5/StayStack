using System.Security.Cryptography;
using System.Text;
namespace BuildingBlocks.Security;

/// <summary>
///     The generate-a-random-token-and-hash-it primitive shared by every
///     "issue a bearer credential, persist only its hash, re-hash an
///     incoming value to look it up" flow (refresh tokens, guest
///     booking-management tokens). Deliberately just these two operations,
///     not a "token service" - what each caller does around them
///     (rotation/reuse detection vs. a single long-lived token) differs
///     enough that a shared abstraction would need to be configurable for
///     behavior most callers don't want.
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
