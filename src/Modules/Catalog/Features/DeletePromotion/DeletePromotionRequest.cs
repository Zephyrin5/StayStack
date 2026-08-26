using Mediator;
namespace Catalog.Features.DeletePromotion;

public record DeletePromotionRequest : IRequest<DeletePromotionResponse>
{
    public Guid PromotionId { get; init; }
}
