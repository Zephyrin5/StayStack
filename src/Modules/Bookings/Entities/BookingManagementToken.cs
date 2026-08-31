namespace Bookings.Entities;

/// <summary>
///     Persistence-layer construct, not a Domain aggregate - same reasoning
///     as UnitAvailabilityHold: no business methods, and a token is simply
///     valid or it isn't, so no soft-delete/audit trail to carry. Issued
///     once, at ConfirmBookingHandler time, only for guest-checkout
///     bookings (CustomerId == null) - an authenticated booking already
///     has account-based proof of ownership. Long-lived and reusable: a
///     guest may revisit the same "manage your booking" link over days or
///     weeks, the opposite of a refresh token's single-use-then-rotate
///     semantics (see SecureToken's own doc comment for why this and
///     refresh tokens share only the generate-and-hash primitive). Not
///     unbounded, though: BookingAccessChecker.ResolveAsync rejects it
///     once CheckOut is far enough in the past - no ExpiresAt column here,
///     since that boundary belongs to the booking, not the token.
/// </summary>
public sealed class BookingManagementToken
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
