using Catalog.Entities;
using Catalog.Enums;
using SeedWork.ValueObjects;
namespace Catalog.Domain;

// Pure, no DB/DI dependency - the one place pricing-rule resolution is
// implemented, called by both HoldAvailabilityHandler (the actual charged
// price) and GetPriceCalendarHandler (the public preview), so the two can
// never structurally disagree. See docs/adr/0012.
//
// Operates in Money throughout (docs/adr/0015) - every nightly price
// rounds to its currency's precision as soon as it's resolved, rather
// than accumulating full-precision decimals and rounding once at the end.
// This makes each night's price the same number ever actually charged,
// and is why StayPriceBreakdown.Subtotal is snapshotted directly rather
// than reconstructed by adding two independently-rounded numbers back
// together.
public static class PricingCalculator
{
    // Resolution order for one night: an active date-range override (if
    // any) is the absolute price, no multiplier stacks on top. Otherwise
    // BasePrice x any matching day-of-week multiplier (or plain
    // BasePrice). Length-of-stay discount is deliberately not applied
    // here - a whole-stay concept a single day doesn't have; see
    // ResolveStayTotal.
    public static Money ResolveNightlyPrice(Money basePrice, DateOnly date, IReadOnlyList<PricingRule> rules)
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
            return Money.Of(overrideRule.OverridePrice!.Value, basePrice.Currency);
        }

        int dayOfWeek = (int)date.DayOfWeek;
        PricingRule? multiplierRule = rules.FirstOrDefault(r =>
            r.RuleType == PricingRuleType.DayOfWeekMultiplier && r.DaysOfWeek!.Contains(dayOfWeek));

        return multiplierRule is not null ? basePrice * multiplierRule.Multiplier!.Value : basePrice;
    }

    // Total for the half-open stay [checkIn, checkOut): sums the nightly
    // price, then applies a length-of-stay discount (if an active rule's
    // MinNights is met). Returns the breakdown, not just the total - a
    // redeemed promo code is exclusive of the LOS discount rather than
    // stacking with it (ConfirmBookingHandler), so callers there need to
    // be able to undo just the LOS portion.
    public static StayPriceBreakdown ResolveStayTotal(
        Money basePrice, DateOnly checkIn, DateOnly checkOut, IReadOnlyList<PricingRule> rules)
    {
        Money subtotal = Money.Of(0m, basePrice.Currency);
        for (DateOnly date = checkIn; date < checkOut; date = date.AddDays(1))
        {
            subtotal += ResolveNightlyPrice(basePrice, date, rules);
        }

        int nights = checkOut.DayNumber - checkIn.DayNumber;
        PricingRule? lengthOfStayRule = rules.FirstOrDefault(r =>
            r.RuleType == PricingRuleType.LengthOfStayDiscount && r.MinNights!.Value <= nights);

        if (lengthOfStayRule is null)
        {
            return new StayPriceBreakdown { Subtotal = subtotal, LengthOfStayDiscountAmount = null, Total = subtotal };
        }

        Money discountAmount = subtotal * (lengthOfStayRule.DiscountPercent!.Value / 100m);
        return new StayPriceBreakdown
        {
            Subtotal = subtotal,
            LengthOfStayDiscountAmount = discountAmount,
            Total = subtotal - discountAmount
        };
    }
}

public sealed record StayPriceBreakdown
{
    public required Money Subtotal { get; init; }
    public Money? LengthOfStayDiscountAmount { get; init; }
    public required Money Total { get; init; }
}
