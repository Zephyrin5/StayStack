using SeedWork.Enums;
namespace Catalog.Entities;

/// <summary>
///     Persistence-layer construct like UnitAvailabilityHold, not a Domain
///     aggregate - EF Core owns its schema (mapped and migrated normally),
///     but the actual insert happens through hand-written Dapper SQL inside
///     the same transaction as the atomic redemption-count increment on
///     Promotion, not through DbContext.SaveChanges() - see
///     Catalog.Contracts' PromotionRedemption implementation. The unique
///     index on (PromotionId, GuestEmail) - see
///     PromotionRedemptionConfiguration - is what actually enforces one
///     redemption per guest per code.
/// </summary>
public sealed class PromotionRedemption
{
    public Guid Id { get; set; }
    public Guid PromotionId { get; set; }

    // Opaque cross-module id, same as UnitAvailabilityHold never
    // referencing Booking directly.
    public Guid BookingId { get; set; }

    // Normalized .Trim().ToLowerInvariant() before insert - the unique
    // index on (PromotionId, GuestEmail) only enforces one-per-email if two
    // different casings of the same address are never allowed to collide
    // as distinct rows.
    public string GuestEmail { get; set; } = string.Empty;

    public decimal DiscountAmount { get; set; }
    public Currency Currency { get; set; }
    public DateTimeOffset RedeemedAt { get; set; }
}
