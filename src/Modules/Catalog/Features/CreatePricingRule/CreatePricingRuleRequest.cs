using Catalog.Enums;
using Mediator;
namespace Catalog.Features.CreatePricingRule;

// One discriminated request for all three rule types rather than three
// parallel request types - only the fields relevant to RuleType are
// required (see CreatePricingRuleRequestValidator); the rest are ignored.
public record CreatePricingRuleRequest : IRequest<CreatePricingRuleResponse>
{
    public Guid UnitId { get; init; }
    public PricingRuleType RuleType { get; init; }

    // DateRangeOverride
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public decimal? OverridePrice { get; init; }

    // DayOfWeekMultiplier
    public int[]? DaysOfWeek { get; init; }
    public decimal? Multiplier { get; init; }

    // LengthOfStayDiscount
    public int? MinNights { get; init; }
    public decimal? DiscountPercent { get; init; }
}
