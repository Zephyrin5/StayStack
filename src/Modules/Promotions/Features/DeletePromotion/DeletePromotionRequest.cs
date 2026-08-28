using Mediator;
namespace Promotions.Features.DeletePromotion;

public record DeletePromotionRequest : IRequest<DeletePromotionResponse>
{
    public Guid PromotionId { get; init; }
}
