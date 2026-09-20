namespace Bookings.Contracts;

/// <summary>
///     Issues and reads the short-lived sessions a guest exchanges their
///     booking-management token for.
///     <para>
///         Declared here and implemented in the Api layer, like
///         <c>BuildingBlocks.Identity.ICurrentUserProvider</c>: minting and validating these tokens
///         needs the signing configuration and the authentication middleware, and the alternative is
///         a module edge docs/adr/0004's direction rule would have to bless for something no part of
///         Bookings needs to know.
///     </para>
/// </summary>
public interface IBookingSessions
{
    /// <summary>
    ///     Mints a session for a booking whose ownership has <em>already</em>
    ///     been proven. This does no checking of its own and must never be
    ///     called before BookingAccessChecker has resolved the booking - it is
    ///     the credential-issuing half, not the credential-checking half.
    /// </summary>
    BookingSession Issue(Guid bookingId);

    /// <summary>
    ///     The booking this request carries a valid session for, or null.
    ///     <para>
    ///         Null covers every failure identically - no token, wrong
    ///         audience, bad signature, expired - because nothing downstream
    ///         benefits from telling them apart and
    ///         <c>BookingAccessChecker</c> already answers "not yours" and
    ///         "doesn't exist" the same way.
    ///     </para>
    /// </summary>
    Task<Guid?> GetSessionBookingIdAsync(CancellationToken cancellationToken);
}

public sealed record BookingSession
{
    public required string SessionToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
}
