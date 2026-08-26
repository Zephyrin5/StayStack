using Catalog.Enums;
using SeedWork.Enums;
namespace Catalog.Features;

public record PromotionSummary
{
    public Guid Id { get; init; }
    public string Code { get; init; } = string.Empty;
    public PromotionDiscountType DiscountType { get; init; }
    public decimal DiscountValue { get; init; }
    public Currency? Currency { get; init; }
    public Guid? HostId { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public int? MaxRedemptions { get; init; }
    public int RedemptionCount { get; init; }
}
