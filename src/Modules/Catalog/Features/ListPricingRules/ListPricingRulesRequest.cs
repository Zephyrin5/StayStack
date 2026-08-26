using Mediator;
namespace Catalog.Features.ListPricingRules;

public record ListPricingRulesRequest : IRequest<ListPricingRulesResponse>
{
    public Guid UnitId { get; init; }
}
