using NpgsqlTypes;
namespace Catalog.Entities;

/// <summary>
///     This is a persistence-layer construct, not a Domain aggregate - its
///     entire shape exists to host the Postgres exclusion constraint that
///     makes double-booking impossible at the database level (see the
///     Initial migration). It has no business methods of its own; business
///     logic never loads it through EF change tracking.
///     EF Core still owns its schema (this entity is mapped and migrated
///     normally) so there's one migration history for the whole database,
///     but HoldAvailabilityHandler writes to it with hand-written Dapper
///     SQL inside an explicit transaction, specifically so it can catch the
///     exclusion-violation exception the constraint throws on overlap. See
///     the discussion on why this table can't be managed through
///     DbContext.SaveChanges() the way Owner/Property/Unit are.
/// </summary>
public sealed class UnitAvailabilityHold
{
    public Guid Id { get; set; }
    public Guid UnitId { get; set; }

    // [CheckIn, CheckOut) - half-open, matches normal hotel-industry
    // date-range semantics (checkout day itself is not occupied).
    public NpgsqlRange<DateOnly> StayRange { get; set; }

    // "held" | "booked" - plain string rather than an enum/EF conversion,
    // since the exclusion constraint's WHERE clause references these
    // literal values directly in raw SQL; keeping the C# side as the same
    // literal strings avoids a translation step to keep in sync.
    public string Status { get; set; } = "held";

    public DateTimeOffset? HoldExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
