using Catalog.Entities;
using Catalog.Entities.Configurations;
using Catalog.Exceptions;
using Persistence;
namespace Catalog.Features;

// Shared by CreatePricingRuleHandler and UpdatePricingRuleHandler - a
// plain method call, not through Mediator, same reasoning
// PropertySummaryMapper gives for staying a shared helper. Rejects
// conflicts at write time instead of resolving them with a
// priority/tie-break concept at read time - see docs/adr/0012.
internal static class PricingRuleOverlapChecker
{
    /// <summary>
    ///     Translates a violation of one of the overlap constraints into the same
    ///     PricingRuleConflictException the in-memory checks throw. Concurrent
    ///     writers both pass the in-memory check, so the constraint decides the
    ///     race and the loser reaches this.
    ///     <para>
    ///         Takes the rule being written because a constraint name says which invariant broke
    ///         and not against what: the tier that collided is on the row, not in the violation.
    ///     </para>
    /// </summary>
    public static bool IsOverlapViolation(Exception exception, PricingRule rule, out string message)
    {
        message = string.Empty;

        if (exception.IsViolationOf(PricingRuleConfiguration.DateRangeOverlapConstraint))
        {
            message = "This date range overlaps an existing active date-range override rule for this unit.";
            return true;
        }

        if (exception.IsViolationOf(PricingRuleConfiguration.LengthOfStayTierIndex))
        {
            message = TierTaken(rule.MinNights);
            return true;
        }

        // Same message as EnsureNoDayOfWeekConflict, so a race decided by the
        // index reads exactly like one caught by the in-memory check.
        if (exception.IsViolationOfAny([.. Enumerable.Range(0, 7).Select(PricingRuleConfiguration.DayOfWeekIndexName)]))
        {
            message = "One or more of these days already has an active day-of-week multiplier rule for this unit.";
            return true;
        }

        return false;
    }

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

    /// <summary>
    ///     Tiers do not overlap the way date ranges do: a rule at 7 nights and one at 30 are both
    ///     meaningful, and PricingCalculator takes the deepest a stay reaches. Two at the same
    ///     threshold are the conflict, because nothing orders them (docs/adr/0012).
    /// </summary>
    public static void EnsureNoLengthOfStayConflict(int minNights, IReadOnlyList<PricingRule> existingLengthOfStayRules)
    {
        if (existingLengthOfStayRules.Any(r => r.MinNights == minNights))
        {
            throw new PricingRuleConflictException(TierTaken(minNights));
        }
    }

    // One wording for both paths, so a race decided by the index reads exactly like one caught in
    // memory - and both name the threshold, which is the part the host has to change.
    private static string TierTaken(int? minNights) =>
        $"This unit already has an active length-of-stay discount starting at {minNights} nights. " +
        "Edit that rule, or choose a different minimum.";
}
