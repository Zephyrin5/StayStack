using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
namespace BuildingBlocks.Security;

/// <summary>
///     Generate a random bearer credential, persist only its hash, re-hash an incoming value to look
///     it up - refresh tokens and guest booking-management tokens both. Two operations rather than a
///     token service: rotation with reuse detection and a single long-lived token differ enough that
///     the shared part is only this.
/// </summary>
public static class SecureToken
{
    /// <summary>
    ///     Base64Url, not standard Base64: a management token travels in a link, and standard Base64's
    ///     <c>+</c>, <c>/</c> and <c>=</c> must be percent-escaped to survive one. Every hop escaping
    ///     correctly is the assumption that eventually fails - a copy-pasted link, a linkifying mail
    ///     client, a redirect that re-encodes, and <c>+</c> arrives as a space.
    /// </summary>
    public static string Generate()
    {
        byte[] randomNumber = new byte[64];
        using RandomNumberGenerator rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Base64Url.EncodeToString(randomNumber);
    }

    /// <summary>
    ///     Standard Base64, unlike <see cref="Generate"/>: this is what is persisted and compared
    ///     server-side, never carried in a URL. Re-encoding it would invalidate every stored hash.
    /// </summary>
    public static string Hash(string token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(token);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
