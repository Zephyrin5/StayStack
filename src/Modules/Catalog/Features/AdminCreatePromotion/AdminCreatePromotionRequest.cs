using Catalog.Enums;
using Catalog.Features.CreatePromotion;
using Mediator;
using SeedWork.Enums;
namespace Catalog.Features.AdminCreatePromotion;

// HostId is an explicit optional body field, not a route segment the way
// AdminCreatePropertyRequest's is - a property always has exactly one
// owning host, but a promotion doesn't (null means platform-wide), so
// there's no host to put in the URL for that case.
public record AdminCreatePromotionRequest : IRequest<CreatePromotionResponse>
{
    public Guid? HostId { get; init; }

    public required string Code { get; init; }
    public PromotionDiscountType DiscountType { get; init; }
    public decimal DiscountValue { get; init; }
    public Currency? Currency { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public int? MaxRedemptions { get; init; }
}
