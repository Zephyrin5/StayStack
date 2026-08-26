namespace Catalog.Features.DeletePricingRule;

public record DeletePricingRuleResponse
{
    public Guid PricingRuleId { get; init; }
}
