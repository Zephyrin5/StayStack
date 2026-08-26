namespace Catalog.Features.DeletePromotion;

public record DeletePromotionResponse
{
    public Guid PromotionId { get; init; }
}
