using Mediator;
namespace Catalog.Features.DeletePricingRule;

public record DeletePricingRuleRequest : IRequest<DeletePricingRuleResponse>
{
    public Guid UnitId { get; init; }
    public Guid PricingRuleId { get; init; }
}
