using Ardalis.GuardClauses;
using Catalog.Enums;
using NpgsqlTypes;
using SeedWork.Abstractions;
using SeedWork.Interfaces;
namespace Catalog.Entities;

// One discriminated entity for all rule types rather than one table per
// type - keeps "give me every rule for this unit" a single query, which is
// what both PricingCalculator's callers need, and mirrors Unit's own
// ad-hoc-fields-over-value-object style (BasePrice/Currency) rather than
// introducing a polymorphic hierarchy nothing else in this module uses. See
// docs/adr/0012. Only the fields relevant to a row's own RuleType are ever
// populated - the others stay null for that row.
public sealed class PricingRule : Entity, IAggregateRoot
{
    private PricingRule(
        Guid id,
        Guid unitId,
        PricingRuleType ruleType,
        NpgsqlRange<DateOnly>? dateRange,
        decimal? overridePrice,
        int[]? daysOfWeek,
        decimal? multiplier,
        int? minNights,
        decimal? discountPercent)
    {
        Id = id;
        UnitId = unitId;
        RuleType = ruleType;
        DateRange = dateRange;
        OverridePrice = overridePrice;
        DaysOfWeek = daysOfWeek;
        Multiplier = multiplier;
        MinNights = minNights;
        DiscountPercent = discountPercent;
    }

    public Guid UnitId { get; private set; }
    public PricingRuleType RuleType { get; private set; }

    // DateRangeOverride only - [StartDate, EndDate), half-open like
    // UnitAvailabilityHold.StayRange.
    public NpgsqlRange<DateOnly>? DateRange { get; private set; }
    public decimal? OverridePrice { get; private set; }

    // DayOfWeekMultiplier only - distinct values 0-6 (System.DayOfWeek).
    public int[]? DaysOfWeek { get; private set; }
    public decimal? Multiplier { get; private set; }

    // LengthOfStayDiscount only.
    public int? MinNights { get; private set; }
    public decimal? DiscountPercent { get; private set; }

    public static PricingRule CreateDateRangeOverride(
        Guid unitId, DateOnly startDate, DateOnly endDate, decimal overridePrice)
    {
        Guard.Against.Default(unitId);
        Guard.Against.NegativeOrZero(overridePrice);
        NpgsqlRange<DateOnly> range = BuildDateRange(startDate, endDate);

        return new PricingRule(
            Guid.CreateVersion7(), unitId, PricingRuleType.DateRangeOverride,
            range, overridePrice, null, null, null, null);
    }

    public static PricingRule CreateDayOfWeekMultiplier(Guid unitId, int[] daysOfWeek, decimal multiplier)
    {
        Guard.Against.Default(unitId);
        Guard.Against.NegativeOrZero(multiplier);
        int[] validated = ValidateDaysOfWeek(daysOfWeek);

        return new PricingRule(
            Guid.CreateVersion7(), unitId, PricingRuleType.DayOfWeekMultiplier,
            null, null, validated, multiplier, null, null);
    }

    public static PricingRule CreateLengthOfStayDiscount(Guid unitId, int minNights, decimal discountPercent)
    {
        Guard.Against.Default(unitId);
        Guard.Against.NegativeOrZero(minNights);
        Guard.Against.OutOfRange(discountPercent, nameof(discountPercent), 0.01m, 100m);

        return new PricingRule(
            Guid.CreateVersion7(), unitId, PricingRuleType.LengthOfStayDiscount,
            null, null, null, null, minNights, discountPercent);
    }

    // One setter per rule-type-specific field, mirroring Unit's SetBasePrice
    // etc. - not a single generic Update(everything), so a caller can't
    // accidentally set fields that don't belong to this row's RuleType.

    public void SetDateRange(DateOnly startDate, DateOnly endDate) => DateRange = BuildDateRange(startDate, endDate);

    public void SetOverridePrice(decimal price)
    {
        Guard.Against.NegativeOrZero(price);
        OverridePrice = price;
    }

    public void SetDaysOfWeek(int[] daysOfWeek) => DaysOfWeek = ValidateDaysOfWeek(daysOfWeek);

    public void SetMultiplier(decimal multiplier)
    {
        Guard.Against.NegativeOrZero(multiplier);
        Multiplier = multiplier;
    }

    public void SetMinNights(int minNights)
    {
        Guard.Against.NegativeOrZero(minNights);
        MinNights = minNights;
    }

    public void SetDiscountPercent(decimal percent)
    {
        Guard.Against.OutOfRange(percent, nameof(percent), 0.01m, 100m);
        DiscountPercent = percent;
    }

    private static NpgsqlRange<DateOnly> BuildDateRange(DateOnly startDate, DateOnly endDate)
    {
        Guard.Against.InvalidInput(
            endDate, nameof(endDate), d => d > startDate, "End date must be after start date.");

        return new NpgsqlRange<DateOnly>(startDate, true, endDate, false);
    }

    private static int[] ValidateDaysOfWeek(int[] daysOfWeek)
    {
        Guard.Against.NullOrEmpty(daysOfWeek);
        if (daysOfWeek.Any(d => d is < 0 or > 6) || daysOfWeek.Distinct().Count() != daysOfWeek.Length)
        {
            throw new ArgumentException("DaysOfWeek must be distinct values between 0 and 6.", nameof(daysOfWeek));
        }

        return daysOfWeek;
    }
}
