using NpgsqlTypes;
using SeedWork.ValueObjects;
namespace Bookings.Entities;

/// <summary>
///     A persistence-layer construct, not a Domain aggregate: its shape exists to host the exclusion
///     constraint that makes double-booking impossible in the database. EF owns its schema, but
///     HoldAvailabilityHandler writes it with Dapper inside an explicit transaction (docs/adr/0010).
/// </summary>
public sealed class UnitAvailabilityHold
{
    /// <summary>
    ///     Sized for the longest value Api.Security.ClientNetworkKey produces, a full-form IPv6 /64.
    ///     The one source of truth for the column's width, so a wider key cannot become a 22001 at
    ///     insert time.
    /// </summary>
    public const int ClientKeyMaxLength = 45;

    public Guid Id { get; set; }

    // Opaque across the boundary: resolved through Catalog.Contracts.IUnitLookup, never joined.
    public Guid UnitId { get; set; }

    public int GuestCount { get; set; }

    // [CheckIn, CheckOut) - the checkout day itself is not occupied.
    public NpgsqlRange<DateOnly> StayRange { get; set; }

    // "held" | "pending_payment" | "booked", a plain string because the handlers compare these
    // literals in hand-written SQL. The exclusion constraint has no WHERE clause, so it applies to
    // every row whatever its status: expired 'held' rows are deleted rather than ignored.
    public string Status { get; set; } = "held";

    public DateTimeOffset? HoldExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    // Diagnostic only: nothing queries it, so do not assume an index or a reader exists.
    public DateTimeOffset? BookedAt { get; set; }

    // The caller's network, normalised by Api.Security.ClientNetworkKey - the one thing here a caller
    // cannot choose. Cleared when the hold is consumed, so it exists only on rows the cap counts.
    public string? ClientKey { get; set; }

    // Snapshotted at hold time, not read live at confirm time: the price a customer saw when they
    // held is the price they get.
    public Money TotalPrice { get; set; }

    // Snapshotted rather than reconstructed from TotalPrice + LengthOfStayDiscountAmount, which is the
    // rounding bug docs/adr/0015 exists to close - each side was independently rounded.
    public decimal Subtotal { get; set; }

    // Part of the same snapshot. A promo code is exclusive of the length-of-stay discount rather than
    // stacking with it, so confirming a hold has to undo this portion of TotalPrice specifically.
    public decimal? LengthOfStayDiscountAmount { get; set; }
}
