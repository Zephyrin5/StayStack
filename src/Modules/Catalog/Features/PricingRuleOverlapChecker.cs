using Catalog.Entities;
using Catalog.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace Catalog.Features;

// Shared by CreatePricingRuleHandler and UpdatePricingRuleHandler - a
// plain method call, not through Mediator, same reasoning
// PropertySummaryMapper gives for staying a shared helper. Rejects
// conflicts at write time instead of resolving them with a
// priority/tie-break concept at read time - see docs/adr/0012.
internal static class PricingRuleOverlapChecker
{
    // The database-side names of the same two invariants this class checks
    // in memory. See the AddPricingRuleOverlapConstraints migration.
    private const string DateRangeOverlapConstraint = "pricing_rules_date_range_overlap_excl";
    private const string LengthOfStayUniqueIndex = "ix_pricing_rules_unit_length_of_stay_active";

    /// <summary>
    ///     Translates a constraint violation into the same
    ///     PricingRuleConflictException the in-memory checks throw.
    ///     <para>
    ///         These constraints were added as a backstop for writers that
    ///         never call this class, on the assumption they could not fire on
    ///         a normal path - so an untranslated 500 seemed like the right
    ///         "this should be impossible" signal. That assumption was wrong,
    ///         and PricingRuleConcurrencyTests caught it: under genuine
    ///         concurrency the losing transaction can surface 23P01 from the
    ///         constraint instead of 40001 from Serializable, and where it
    ///         used to retry and produce a clean 409 it produced a 500. Two
    ///         hosts editing the same unit at the same moment is ordinary use,
    ///         not an impossible path.
    ///     </para>
    /// </summary>
    public static bool IsOverlapViolation(Exception exception, out string message)
    {
        message = string.Empty;

        if (exception is not DbUpdateException { InnerException: PostgresException postgres })
        {
            return false;
        }

        if (postgres.SqlState == PostgresErrorCodes.ExclusionViolation
            && postgres.ConstraintName == DateRangeOverlapConstraint)
        {
            message = "This date range overlaps an existing active date-range override rule for this unit.";
            return true;
        }

        if (postgres.SqlState == PostgresErrorCodes.UniqueViolation
            && postgres.ConstraintName == LengthOfStayUniqueIndex)
        {
            message = "This unit already has an active length-of-stay discount rule - only one is allowed at a time.";
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

    public static void EnsureNoLengthOfStayConflict(IReadOnlyList<PricingRule> existingLengthOfStayRules)
    {
        if (existingLengthOfStayRules.Count > 0)
        {
            throw new PricingRuleConflictException(
                "This unit already has an active length-of-stay discount rule - only one is allowed at a time.");
        }
    }
}
