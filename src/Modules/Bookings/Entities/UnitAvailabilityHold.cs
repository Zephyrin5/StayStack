using NpgsqlTypes;
using SeedWork.ValueObjects;
namespace Bookings.Entities;

/// <summary>
///     A persistence-layer construct, not a Domain aggregate - its shape
///     exists to host the Postgres exclusion constraint that makes
///     double-booking impossible at the database level. No business
///     methods; business logic never loads it through EF change tracking.
///     EF still owns its schema (mapped and migrated normally), but
///     HoldAvailabilityHandler writes to it with hand-written Dapper SQL
///     inside an explicit transaction instead of SaveChanges() - see
///     docs/adr/0010 for why.
/// </summary>
public sealed class UnitAvailabilityHold
{
    /// <summary>
    ///     Sized for the longest value Api.Security.ClientNetworkKey can
    ///     produce - a full-form IPv6 /64 ("xxxx:xxxx:xxxx:xxxx::/64", 42
    ///     chars). The single source of truth for that column's width, so a
    ///     wider key can't silently become a Postgres 22001 at insert time.
    /// </summary>
    public const int ClientKeyMaxLength = 45;

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

    // "held" | "booked" - plain string, not an enum/EF conversion, since
    // HoldAvailabilityHandler/HoldConfirmation reference these literals
    // directly in hand-written SQL. NOTE: the exclusion constraint has no
    // WHERE clause - it applies to every row regardless of status/expiry,
    // which is why stale 'held' rows past hold_expires_at must be actively
    // deleted (HoldAvailabilityHandler's cleanup DELETE) rather than
    // relying on the constraint to ignore them.
    public string Status { get; set; } = "held";

    public DateTimeOffset? HoldExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Set when the hold becomes 'booked' (HoldConfirmation.MarkHoldPaidAsync),
    // cleared on release. Diagnostic only: nothing queries it, so do not assume
    // an index or a reader exists.
    public DateTimeOffset? BookedAt { get; set; }


    // The caller's network, normalised by Api.Security.ClientNetworkKey.
    // Counted by HoldAvailabilityHandler's concurrent-hold cap - the one
    // thing here a caller can't choose for themselves.
    //
    // Cleared when the hold is consumed (HoldConfirmation.ConfirmHoldAsync),
    // so this only ever exists on rows the cap can actually count: a live
    // hold, for at most its 15-minute expiry. A booked hold keeps no record
    // of the address that made it, which is the point - the cap needs it,
    // and a booking that outlives the hold by years does not. Also null for
    // any hold predating this column.
    public string? ClientKey { get; set; }

    // Snapshotted from the unit at hold-creation time, not read live at
    // confirm time - a unit's price can change between holding and
    // confirming; the price a customer saw when they held is the price
    // they get. The one Money-typed field here - Subtotal/
    // LengthOfStayDiscountAmount below are plain decimals in this same
    // currency by construction.
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
