using Catalog.Entities;
using Catalog.Exceptions;
namespace Catalog.Features;

// Shared by CreatePricingRuleHandler and UpdatePricingRuleHandler - a plain
// method call, not through Mediator, same reasoning PropertySummaryMapper
// gives for staying a shared helper rather than its own request. v1
// rejects conflicts at write time instead of resolving them with a
// priority/tie-break concept at read time - see docs/adr/0012.
internal static class PricingRuleOverlapChecker
{
    public static void EnsureNoDateRangeConflict(DateOnly startDate, DateOnly endDate, IReadOnlyList<PricingRule> existingOverrides)
    {
        bool conflicts = existingOverrides.Any(r =>
            startDate < r.DateRange!.Value.UpperBound && r.DateRange.Value.LowerBound < endDate);

        if (conflicts)
        {
            throw new PricingRuleConflictException(
                "This date range overlaps an existing active date-range override rule for this unit.");
        }
    }

    public static void EnsureNoDayOfWeekConflict(int[] daysOfWeek, IReadOnlyList<PricingRule> existingMultipliers)
    {
        bool conflicts = existingMultipliers.Any(r => r.DaysOfWeek!.Intersect(daysOfWeek).Any());

        if (conflicts)
        {
            throw new PricingRuleConflictException(
                "One or more of these days already has an active day-of-week multiplier rule for this unit.");
        }
    }

    public static void EnsureNoLengthOfStayConflict(IReadOnlyList<PricingRule> existingLengthOfStayRules)
    {
        if (existingLengthOfStayRules.Count > 0)
        {
            throw new PricingRuleConflictException(
                "This unit already has an active length-of-stay discount rule - only one is allowed at a time.");
        }
    }
}
