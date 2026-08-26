using Catalog.Enums;
namespace Catalog.Features.ListPricingRules;

public record ListPricingRulesResponse
{
    public List<PricingRuleSummary> Rules { get; init; } = [];
}

// Unpaged - rule counts per unit are small (a handful of seasonal
// overrides, a handful of weekday-multiplier rules, at most one
// length-of-stay rule per docs/adr/0012), unlike the property/booking list
// endpoints that need PagedResponse.
public record PricingRuleSummary
{
    public Guid PricingRuleId { get; init; }
    public PricingRuleType RuleType { get; init; }

    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public decimal? OverridePrice { get; init; }

    public int[]? DaysOfWeek { get; init; }
    public decimal? Multiplier { get; init; }

    public int? MinNights { get; init; }
    public decimal? DiscountPercent { get; init; }
}
