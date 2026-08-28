using Mediator;
using SeedWork.Enums;
namespace Promotions.Features.UpdatePromotion;

// No Code or DiscountType here - both are immutable after creation (see
// Promotion's own doc comment). Currency's FixedAmount-only requirement is
// enforced by Promotion.SetCurrency against the existing (unchangeable)
// DiscountType, not re-derived here - the validator stays shape-only, same
// convention as every other request validator in this module.
public record UpdatePromotionRequest : IRequest<UpdatePromotionResponse>
{
    public Guid PromotionId { get; init; }
    public decimal DiscountValue { get; init; }
    public Currency? Currency { get; init; }
    public DateTimeOffset? ExpiresAt { get; init; }
    public int? MaxRedemptions { get; init; }
}
