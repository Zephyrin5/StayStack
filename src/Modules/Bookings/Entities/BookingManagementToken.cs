namespace Bookings.Entities;

/// <summary>
///     Persistence-layer construct, not a Domain aggregate - same reasoning
///     as UnitAvailabilityHold: no business methods, and a token is simply
///     valid or it isn't. Issued at checkout (and again on replay) only for
///     guest-checkout bookings (CustomerId == null); an authenticated booking
///     has account-based proof of ownership. Long-lived and reusable: a guest
///     may revisit the same "manage your booking" link over days or weeks,
///     unlike a single-use refresh token (see SecureToken). Bounded by the
///     booking, not the token: BookingAccessChecker rejects it once CheckOut
///     is far enough in the past, so there is no ExpiresAt column.
/// </summary>
public sealed class BookingManagementToken
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
