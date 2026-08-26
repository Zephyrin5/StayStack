namespace Bookings.Entities;

/// <summary>
///     Persistence-layer construct, not a Domain aggregate - same reasoning
///     as UnitAvailabilityHold (Catalog): it has no business methods of its
///     own, and a token is simply valid or it isn't, so there's no
///     soft-delete/audit trail to carry (unlike Entity-derived aggregates).
///     Issued once, at ConfirmBookingHandler time, only for guest-checkout
///     bookings (CustomerId == null) - an authenticated booking already has
///     account-based proof of ownership and never gets one. Long-lived and
///     reusable by design: a guest may revisit the same "manage your
///     booking" link over days or weeks, the opposite of a refresh token's
///     single-use-then-rotate semantics (see BuildingBlocks.Security.SecureToken's
///     own doc comment for why this and refresh tokens share only the
///     generate-and-hash primitive, not a full "token service").
/// </summary>
public sealed class BookingManagementToken
{
    public Guid Id { get; set; }
    public Guid BookingId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}
