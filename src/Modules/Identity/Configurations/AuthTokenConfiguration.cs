using System.ComponentModel.DataAnnotations;
﻿namespace Identity.Configurations;

public class AuthTokenConfiguration
{
    public const string SectionName = "Auth:Token";

    /// <summary>
    ///     The HMAC-SHA256 signing secret, used as raw UTF-8 bytes by both
    ///     AuthTokenProvider (signing) and IdentityServicesRegistration's
    ///     TokenValidationParameters (validation).
    ///     <para>
    ///         Required alone left a gap: SymmetricSecurityKey's constructor
    ///         only rejects a zero-length key, and the 128-bit floor HS256
    ///         actually requires is enforced later, by SymmetricSignatureProvider
    ///         when a token is first signed. So any non-empty key shorter than
    ///         that started the app cleanly and then failed the first sign-in -
    ///         configuration surfacing as a runtime error, on the auth path,
    ///         which is the shape ValidateOnStart exists to convert into a boot
    ///         failure.
    ///     </para>
    ///     <para>
    ///         32 rather than the 16 that floor implies: MinLength counts
    ///         characters and the key is UTF-8 encoded, and since no character
    ///         encodes to less than a byte, 32 characters guarantees at least
    ///         256 bits - matching SHA-256's own output size, which is the
    ///         usual recommendation for an HMAC key rather than the bare
    ///         minimum the algorithm will accept.
    ///     </para>
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    [MinLength(32, ErrorMessage =
        "Auth:Token:Key must be at least 32 characters. HMAC-SHA256 requires at least 128 bits of key " +
        "material, and a shorter key would start the application but fail the first sign-in.")]
    public string Key { get; set; } = string.Empty;
    public string? Issuer { get; set; }
    public string? Audience { get; set; }
    [Range(1, 1440)]
    public double AccessTokenLifespanInMinutes { get; set; }
    [Range(1, 3650)]
    public double RefreshTokenLifespanInDays { get; set; }
}
