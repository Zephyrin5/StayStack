namespace BuildingBlocks.Security;

/// <summary>
///     Authentication scheme names this app registers beyond the default
///     bearer scheme.
///     <para>
///         In BuildingBlocks rather than in Identity because both ends need
///         it: Identity registers the scheme, and the Api layer names it when
///         authenticating a booking-management request. Neither references the
///         other.
///     </para>
/// </summary>
public static class AuthenticationSchemes
{
    /// <summary>
    ///     Short-lived sessions a guest exchanges their booking-management
    ///     token for. A separate scheme, not a claim on the default one - see
    ///     AuthTokenConfiguration.BookingSessionAudience for why that
    ///     distinction is the security boundary rather than a detail.
    /// </summary>
    public const string BookingSession = "BookingSession";
}
