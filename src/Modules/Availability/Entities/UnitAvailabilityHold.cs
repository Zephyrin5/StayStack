using NpgsqlTypes;
using SeedWork.ValueObjects;
namespace Availability.Entities;

/// <summary>
///     This is a persistence-layer construct, not a Domain aggregate - its
///     entire shape exists to host the Postgres exclusion constraint that
///     makes double-booking impossible at the database level. It has no
///     business methods of its own; business logic never loads it through
///     EF change tracking. EF Core still owns its schema (mapped and
///     migrated normally, one migration history for the whole database),
///     but HoldAvailabilityHandler writes to it with hand-written Dapper
///     SQL inside an explicit transaction instead of DbContext.SaveChanges()
///     the way most entities are - see docs/adr/0010 for why.
/// </summary>
public sealed class UnitAvailabilityHold
{
    public Guid Id { get; set; }

    // Opaque cross-module id, resolved through Catalog.Contracts.IUnitLookup
    // when this module needs a fact about the unit itself (price,
    // capacity) - never a navigation property or a join against Catalog's
    // own tables.
    public Guid UnitId { get; set; }

    public int GuestCount { get; set; }

    // [CheckIn, CheckOut) - half-open, matches normal hotel-industry
    // date-range semantics (checkout day itself is not occupied).
    public NpgsqlRange<DateOnly> StayRange { get; set; }

    // "held" | "booked" - plain string rather than an enum/EF conversion,
    // since HoldAvailabilityHandler/HoldConfirmation reference these literal
    // values directly in hand-written SQL; keeping the C# side as the same
    // literal strings avoids a translation step to keep in sync. NOTE: the
    // exclusion constraint itself has no WHERE clause - it applies to every
    // row regardless of status/expiry, which is exactly why stale 'held'
    // rows past hold_expires_at must be actively deleted (see
    // HoldAvailabilityHandler's cleanup DELETE) rather than relying on the
    // constraint to ignore them.
    public string Status { get; set; } = "held";

    public DateTimeOffset? HoldExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Set only when Status transitions to "booked" (HoldConfirmation.
    // ConfirmHoldAsync), cleared back to null on release - the grace-period
    // anchor Bookings.Jobs.ReconcileOrphanedBookedHoldsJob uses to find a
    // 'booked' hold with no matching booking (a process crash between
    // ConfirmHoldAsync and the Booking insert, see docs/adr/0003). Never set
    // at creation time, unlike CreatedAt.
    public DateTimeOffset? BookedAt { get; set; }

    // An opaque per-browser correlator, not an identity - see
    // Api.Security.HoldSessionCookie and docs/adr/0016. Null for any hold
    // predating this column.
    public string? HolderToken { get; set; }

    // Snapshotted from the unit at hold-creation time (via
    // Catalog.Contracts.IUnitLookup.ResolveStayPricingAsync), not read live
    // at confirm time - a unit's price can change between a customer
    // holding a range and confirming it; the price they saw when they held
    // it is the price they get. The one Money-typed (currency-carrying)
    // field on this entity - Subtotal/LengthOfStayDiscountAmount below are
    // plain decimals in this same currency by construction (a hold has
    // exactly one currency), not independently-currencied amounts.
    public Money TotalPrice { get; set; }

    // The pre-discount total, snapshotted directly rather than left for a
    // caller to reconstruct via TotalPrice + LengthOfStayDiscountAmount -
    // that reconstruction is exactly the rounding bug docs/adr/0015 exists
    // to close, since each side was independently rounded.
    public decimal Subtotal { get; set; }

    // Part of the same snapshot as TotalPrice/Subtotal, for the same
    // reason - a redeemed promo code is exclusive of the length-of-stay
    // discount rather than stacking with it (see ConfirmBookingHandler),
    // so confirming a hold needs to be able to undo just this portion of
    // TotalPrice, not only read the final number. Null when no LOS
    // discount applied to this stay.
    public decimal? LengthOfStayDiscountAmount { get; set; }
}
