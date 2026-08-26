using NpgsqlTypes;
using SeedWork.Enums;
namespace Catalog.Entities;

/// <summary>
///     This is a persistence-layer construct, not a Domain aggregate - its
///     entire shape exists to host the Postgres exclusion constraint that
///     makes double-booking impossible at the database level. It has no
///     business methods of its own; business logic never loads it through
///     EF change tracking. EF Core still owns its schema (mapped and
///     migrated normally, one migration history for the whole database),
///     but HoldAvailabilityHandler writes to it with hand-written Dapper
///     SQL inside an explicit transaction instead of DbContext.SaveChanges()
///     the way Owner/Property/Unit are - see docs/adr/0010 for why.
/// </summary>
public sealed class UnitAvailabilityHold
{
    public Guid Id { get; set; }
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

    // Snapshotted from the unit at hold-creation time, not read live at
    // confirm time - Unit.BasePrice can change (SetBasePrice) between a
    // customer holding a range and confirming it; the price they saw when
    // they held it is the price they get.
    public decimal TotalPrice { get; set; }
    public Currency Currency { get; set; }

    // Part of the same snapshot as TotalPrice/Currency, for the same
    // reason - a redeemed promo code is exclusive of the length-of-stay
    // discount rather than stacking with it (see ConfirmBookingHandler),
    // so confirming a hold needs to be able to undo just this portion of
    // TotalPrice, not only read the final number. Null when no LOS
    // discount applied to this stay.
    public decimal? LengthOfStayDiscountAmount { get; set; }
}
