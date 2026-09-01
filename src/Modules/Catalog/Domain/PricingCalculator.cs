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

        // ROUNDING DECISION, stated because it is a real choice and the
        // alternative is the more common one.
        //
        // The discount is applied to the already-rounded subtotal and is
        // itself rounded, so Subtotal, LengthOfStayDiscountAmount and Total
        // are all payable amounts and Total is exactly
        // Subtotal - LengthOfStayDiscountAmount. The usual alternative -
        // carry full precision throughout, round once at the boundary - is
        // rejected here for the same reason ADR-0015 rejected it one level
        // down for per-night prices: the guest is shown the subtotal and the
        // discount, so those two must reconcile with what they are charged.
        //
        // How much this actually matters, measured rather than assumed:
        // because the subtotal is always an exact multiple of the minor unit
        // (a sum of already-rounded nightly prices), rounding is otherwise
        // translation-invariant and the two policies agree. They part company
        // only at exact ties, where MidpointRounding.ToEven looks at the last
        // digit of the discount in one policy and of the difference in the
        // other, and those can have different parity. A sweep over 200k
        // random stays put that at ~0.02% of cases, always by exactly one
        // minor unit. PricingCalculatorTests pins a concrete instance:
        // 45 nights at 191.175 KWD less 13.2% is 7467.295 here and 7467.296
        // under round-at-the-end - and at that tie, round-at-the-end is also
        // precisely where the itemised bill stops adding up.
        //
        // The percentage is collapsed to a single decimal factor before it
        // touches Money, deliberately: Money rounds on every operator, so
        // `subtotal * percent / 100m` would round the intermediate
        // multiplication too and is not the same value. See Money's own doc
        // comment.
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
