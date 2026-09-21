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
    ///         [Required] alone left a gap: SymmetricSecurityKey rejects only a zero-length key, and
    ///         HS256's 128-bit floor is enforced when a token is first signed - so a short key started
    ///         the app cleanly and failed the first sign-in, which is the shape ValidateOnStart exists
    ///         to turn into a boot failure.
    ///     </para>
    ///     <para>
    ///         32 characters rather than the 16 that floor implies: the length counts characters and
    ///         the key is UTF-8, so 32 guarantees at least 256 bits - SHA-256's own output size, the
    ///         usual recommendation rather than the minimum the algorithm accepts.
    ///     </para>
    /// </summary>
    [Required(AllowEmptyStrings = false)]
    // StringLength rather than MinLength: MinLength's reflective path for
    // non-collection types makes it a trimming hazard (docs/adr/0001).
    [StringLength(int.MaxValue, MinimumLength = 32, ErrorMessage =
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
    ///     How long after a rotation the token it consumed is still read as that rotation's
    ///     duplicate rather than as reuse.
    ///     <para>
    ///         Two tabs sharing a refresh cookie, or one client answering two parallel 401s, present
    ///         the same token twice. Exactly one rotates it; the other arrives at a token that is
    ///         revoked and replaced, which is also what a replayed stolen token looks like. Inside
    ///         this window the benign reading wins and the request is refused without revoking the
    ///         family, so the rotation that just succeeded survives (docs/adr/0009).
    ///     </para>
    ///     <para>
    ///         Seconds, not minutes: it is sized for requests already in flight together, and every
    ///         second of it is a second in which a stolen token is refused without being detected.
    ///     </para>
    /// </summary>
    [Range(1, 120)]
    public int RotationReuseGraceSeconds { get; set; } = 30;

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
    ///         The security boundary, and deliberately not a claim the application checks: both tokens
    ///         are signed with the same key, so a <c>scope</c> claim would only be enforced where
    ///         somebody remembered to look - and the bearer scheme would have built an authenticated
    ///         principal first. Audience validation happens inside that scheme, so a booking session
    ///         presented to a normal endpoint never becomes a principal at all.
    ///     </para>
    ///     <para>
    ///         Derived from <see cref="Audience"/> rather than configured
    ///         separately, so the two cannot be set equal by accident - which
    ///         would silently collapse the boundary.
    ///     </para>
    /// </summary>
    public string BookingSessionAudience => $"{Audience}/booking-session";
}
