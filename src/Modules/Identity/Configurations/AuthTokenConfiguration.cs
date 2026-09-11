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

    /// <summary>
    ///     How long a booking-management session lasts once a guest exchanges
    ///     their management token for one.
    ///     <para>
    ///         Short because it carries no revocation. A booking session is a
    ///         signed claim, not a row, so nothing can retract one inside its
    ///         window - the same trade the access token already makes, and
    ///         acceptable for the same reason: the window is short enough that
    ///         waiting it out is the remedy. The long-lived management token
    ///         behind it stays revocable in the only way it ever was, by
    ///         deleting its row.
    ///     </para>
    /// </summary>
    [Range(5, 240)]
    public double BookingSessionLifetimeInMinutes { get; set; } = 45;

    /// <summary>
    ///     The audience stamped on booking-session tokens, and the reason one
    ///     can never be used as an access token.
    ///     <para>
    ///         This is the actual security boundary between the two, and it is
    ///         deliberately not a claim the application checks. Both tokens are
    ///         signed with the same key by the same issuer, so a
    ///         <c>scope</c> claim would only be enforced where somebody
    ///         remembered to look - and the default bearer scheme, which every
    ///         <c>[Authorize]</c> endpoint runs through, would have validated
    ///         the signature and built an authenticated principal long before
    ///         any handler got the chance. Audience validation happens inside
    ///         that scheme: a booking session presented to a normal endpoint
    ///         fails <c>ValidAudience</c> and never becomes a principal at all.
    ///     </para>
    ///     <para>
    ///         Derived from <see cref="Audience"/> rather than configured
    ///         separately, so the two cannot be set equal by accident - which
    ///         would silently collapse the boundary.
    ///     </para>
    /// </summary>
    public string BookingSessionAudience => $"{Audience}/booking-session";
}
