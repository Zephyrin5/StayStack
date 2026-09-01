using System.Buffers.Text;
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
    /// <summary>
    ///     Base64Url, not standard Base64. A booking-management token is
    ///     handed to a guest as a link
    ///     (<c>/bookings/manage/{id}?managementToken=...</c>), and standard
    ///     Base64's <c>+</c>, <c>/</c> and <c>=</c> all have to be
    ///     percent-escaped to survive one. The client does escape it, so this
    ///     was not broken - but it only stayed unbroken while every hop
    ///     handled the encoding correctly, and a <c>+</c> silently decoding
    ///     back as a space is the classic way that stops being true (a
    ///     copy-pasted link, an auto-linkifying mail client, a redirect that
    ///     re-encodes). An alphabet with nothing to escape removes the class
    ///     of bug rather than relying on every hop.
    ///     <para>
    ///         Safe to change in place: only <see cref="Hash"/> output is ever
    ///         persisted, so tokens already issued keep validating - they hash
    ///         the same as they always did.
    ///     </para>
    /// </summary>
    public static string Generate()
    {
        byte[] randomNumber = new byte[64];
        using RandomNumberGenerator rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        return Base64Url.EncodeToString(randomNumber);
    }

    /// <summary>
    ///     Deliberately still standard Base64, unlike <see cref="Generate"/>.
    ///     This value is what gets persisted (refresh_tokens.token_hash,
    ///     booking_management_tokens.token_hash) and is only ever compared
    ///     server-side against a freshly computed hash - it never reaches a
    ///     URL, so it gains nothing from a URL-safe alphabet. Re-encoding it
    ///     would invalidate every hash already stored, logging out every
    ///     session and breaking every outstanding management link, for no
    ///     benefit.
    /// </summary>
    public static string Hash(string token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(token);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
