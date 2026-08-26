using Catalog.Enums;
using Mediator;
namespace Catalog.Features.UpdatePricingRule;

// Same discriminated shape as CreatePricingRuleRequest - RuleType cannot be
// changed on update (the handler rejects a mismatch against the existing
// rule's RuleType), it's only here so the validator can apply the same
// per-type field rules without a second copy of them.
public record UpdatePricingRuleRequest : IRequest<UpdatePricingRuleResponse>
{
    public Guid UnitId { get; init; }
    public Guid PricingRuleId { get; init; }
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
