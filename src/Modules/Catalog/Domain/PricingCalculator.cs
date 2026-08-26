using Catalog.Entities;
using Catalog.Enums;
namespace Catalog.Domain;

// Pure, no DB/DI dependency - the one place pricing-rule resolution is
// implemented, called by both HoldAvailabilityHandler (the actual charged
// price, snapshotted onto a hold) and GetPriceCalendarHandler (the public
// preview), so the two can never structurally disagree. See docs/adr/0012.
public static class PricingCalculator
{
    // Resolution order for a single night: an active date-range override
    // (if any) is the absolute price - no multiplier stacks on top of it.
    // Otherwise, BasePrice x any active day-of-week multiplier matching
    // that weekday (or just BasePrice if none matches). Length-of-stay
    // discount is deliberately NOT applied here - it's a whole-stay
    // concept a single calendar day doesn't have; see ResolveStayTotal.
    public static decimal ResolveNightlyPrice(decimal basePrice, DateOnly date, IReadOnlyList<PricingRule> rules)
    {
        // Constructed as [LowerBound, UpperBound) everywhere a DateRange is
        // built (see PricingRule.BuildDateRange), so a plain half-open
        // comparison on the bounds is equivalent to or.Contains(date)
        // without depending on NpgsqlRange<T>'s own Contains overload set.
        PricingRule? overrideRule = rules.FirstOrDefault(r =>
            r.RuleType == PricingRuleType.DateRangeOverride
            && date >= r.DateRange!.Value.LowerBound && date < r.DateRange.Value.UpperBound);

        if (overrideRule is not null)
        {
            return overrideRule.OverridePrice!.Value;
        }

        int dayOfWeek = (int)date.DayOfWeek;
        PricingRule? multiplierRule = rules.FirstOrDefault(r =>
            r.RuleType == PricingRuleType.DayOfWeekMultiplier && r.DaysOfWeek!.Contains(dayOfWeek));

        return multiplierRule is not null ? basePrice * multiplierRule.Multiplier!.Value : basePrice;
    }

    // Total for the half-open stay [checkIn, checkOut): sums the nightly
    // price for every night, then applies a length-of-stay discount (if an
    // active rule's MinNights is met) to that subtotal.
    public static decimal ResolveStayTotal(
        decimal basePrice, DateOnly checkIn, DateOnly checkOut, IReadOnlyList<PricingRule> rules)
    {
        decimal subtotal = 0m;
        for (DateOnly date = checkIn; date < checkOut; date = date.AddDays(1))
        {
            subtotal += ResolveNightlyPrice(basePrice, date, rules);
        }

        int nights = checkOut.DayNumber - checkIn.DayNumber;
        PricingRule? lengthOfStayRule = rules.FirstOrDefault(r =>
            r.RuleType == PricingRuleType.LengthOfStayDiscount && r.MinNights!.Value <= nights);

        return lengthOfStayRule is not null
            ? subtotal * (1 - lengthOfStayRule.DiscountPercent!.Value / 100m)
            : subtotal;
    }
}
