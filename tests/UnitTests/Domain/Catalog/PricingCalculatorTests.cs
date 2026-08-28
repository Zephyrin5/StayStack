using Catalog.Domain;
using Catalog.Entities;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace UnitTests.Domain.Catalog;

public class PricingCalculatorTests
{
    private static readonly Guid UnitId = Guid.NewGuid();

    // USD (2 decimal places) for every test whose numbers were already
    // exact to begin with - KWD's 3-decimal rounding is exercised
    // separately below, where it actually changes the result.
    private static Money Usd(decimal amount) => Money.Of(amount, Currency.USD);

    [Fact]
    public void ResolveNightlyPrice_ShouldReturnBasePrice_WhenNoRules()
    {
        Money price = PricingCalculator.ResolveNightlyPrice(Usd(100m), new DateOnly(2026, 1, 1), []);

        Assert.Equal(Usd(100m), price);
    }

    [Fact]
    public void ResolveNightlyPrice_ShouldReturnOverridePrice_WhenDateInsideActiveOverride()
    {
        DateOnly date = new DateOnly(2026, 12, 25);
        PricingRule rule = PricingRule.CreateDateRangeOverride(
            UnitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 31), 250m);

        Money price = PricingCalculator.ResolveNightlyPrice(Usd(100m), date, [rule]);

        Assert.Equal(Usd(250m), price);
    }

    [Fact]
    public void ResolveNightlyPrice_ShouldIgnoreMultiplier_WhenOverrideAlsoApplies()
    {
        DateOnly date = new DateOnly(2026, 12, 25); // a Friday
        PricingRule overrideRule = PricingRule.CreateDateRangeOverride(
            UnitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 31), 250m);
        PricingRule multiplierRule = PricingRule.CreateDayOfWeekMultiplier(UnitId, [5, 6], 1.5m);

        Money price = PricingCalculator.ResolveNightlyPrice(Usd(100m), date, [overrideRule, multiplierRule]);

        Assert.Equal(Usd(250m), price);
    }

    [Fact]
    public void ResolveNightlyPrice_ShouldApplyMultiplier_WhenWeekdayMatches()
    {
        DateOnly saturday = new DateOnly(2026, 8, 29);
        PricingRule rule = PricingRule.CreateDayOfWeekMultiplier(UnitId, [(int)DayOfWeek.Saturday], 1.5m);

        Money price = PricingCalculator.ResolveNightlyPrice(Usd(100m), saturday, [rule]);

        Assert.Equal(Usd(150m), price);
    }

    [Fact]
    public void ResolveNightlyPrice_ShouldReturnBasePrice_WhenWeekdayDoesNotMatch()
    {
        DateOnly tuesday = new DateOnly(2026, 9, 1);
        PricingRule rule = PricingRule.CreateDayOfWeekMultiplier(UnitId, [(int)DayOfWeek.Saturday], 1.5m);

        Money price = PricingCalculator.ResolveNightlyPrice(Usd(100m), tuesday, [rule]);

        Assert.Equal(Usd(100m), price);
    }

    [Fact]
    public void ResolveStayTotal_ShouldSumMixOfOverrideAndBaseNights()
    {
        // Aug 29 (Sat, overridden) + Aug 30/31 (base) = 200 + 100 + 100
        DateOnly checkIn = new DateOnly(2026, 8, 29);
        DateOnly checkOut = new DateOnly(2026, 9, 1);
        PricingRule overrideRule = PricingRule.CreateDateRangeOverride(
            UnitId, new DateOnly(2026, 8, 29), new DateOnly(2026, 8, 30), 200m);

        StayPriceBreakdown breakdown = PricingCalculator.ResolveStayTotal(Usd(100m), checkIn, checkOut, [overrideRule]);

        Assert.Equal(Usd(400m), breakdown.Total);
        Assert.Equal(Usd(400m), breakdown.Subtotal);
        Assert.Null(breakdown.LengthOfStayDiscountAmount);
    }

    [Fact]
    public void ResolveStayTotal_ShouldApplyMultiplierOnlyOnMatchingNights()
    {
        // Week of Mon Aug 24 - Sun Aug 30 2026: Fri 28 + Sat 29 are weekend nights.
        DateOnly checkIn = new DateOnly(2026, 8, 24);
        DateOnly checkOut = new DateOnly(2026, 8, 31); // 7 nights
        PricingRule weekendRule = PricingRule.CreateDayOfWeekMultiplier(
            UnitId, [(int)DayOfWeek.Friday, (int)DayOfWeek.Saturday], 2m);

        StayPriceBreakdown breakdown = PricingCalculator.ResolveStayTotal(Usd(100m), checkIn, checkOut, [weekendRule]);

        // 5 base nights @ 100 + 2 weekend nights @ 200
        Assert.Equal(Usd(900m), breakdown.Total);
    }

    [Fact]
    public void ResolveStayTotal_ShouldNotApplyDiscount_WhenNightsBelowThreshold()
    {
        DateOnly checkIn = new DateOnly(2026, 1, 1);
        DateOnly checkOut = new DateOnly(2026, 1, 7); // 6 nights
        PricingRule discountRule = PricingRule.CreateLengthOfStayDiscount(UnitId, 7, 10m);

        StayPriceBreakdown breakdown = PricingCalculator.ResolveStayTotal(Usd(100m), checkIn, checkOut, [discountRule]);

        Assert.Equal(Usd(600m), breakdown.Total);
        Assert.Null(breakdown.LengthOfStayDiscountAmount);
    }

    [Fact]
    public void ResolveStayTotal_ShouldApplyDiscount_WhenNightsMeetThresholdExactly()
    {
        DateOnly checkIn = new DateOnly(2026, 1, 1);
        DateOnly checkOut = new DateOnly(2026, 1, 8); // 7 nights
        PricingRule discountRule = PricingRule.CreateLengthOfStayDiscount(UnitId, 7, 10m);

        StayPriceBreakdown breakdown = PricingCalculator.ResolveStayTotal(Usd(100m), checkIn, checkOut, [discountRule]);

        Assert.Equal(Usd(700m), breakdown.Subtotal);
        Assert.Equal(Usd(70m), breakdown.LengthOfStayDiscountAmount);
        Assert.Equal(Usd(630m), breakdown.Total); // 700 * 0.9
    }

    [Fact]
    public void ResolveStayTotal_ShouldApplyDiscount_WhenNightsExceedThreshold()
    {
        DateOnly checkIn = new DateOnly(2026, 1, 1);
        DateOnly checkOut = new DateOnly(2026, 1, 15); // 14 nights
        PricingRule discountRule = PricingRule.CreateLengthOfStayDiscount(UnitId, 7, 10m);

        StayPriceBreakdown breakdown = PricingCalculator.ResolveStayTotal(Usd(100m), checkIn, checkOut, [discountRule]);

        Assert.Equal(Usd(1260m), breakdown.Total); // 1400 * 0.9
    }

    [Fact]
    public void ResolveStayTotal_ShouldCombineOverrideMultiplierAndDiscount()
    {
        // Mon Aug 24 - Mon Aug 31 2026: 7 nights.
        // Fri 28/Sat 29 are weekend nights (x2). Aug 29 is also date-range
        // overridden to 500 (override wins over the multiplier for that night).
        // 7 nights total triggers the 10% length-of-stay discount on the subtotal.
        DateOnly checkIn = new DateOnly(2026, 8, 24);
        DateOnly checkOut = new DateOnly(2026, 8, 31);
        PricingRule overrideRule = PricingRule.CreateDateRangeOverride(
            UnitId, new DateOnly(2026, 8, 29), new DateOnly(2026, 8, 30), 500m);
        PricingRule weekendRule = PricingRule.CreateDayOfWeekMultiplier(
            UnitId, [(int)DayOfWeek.Friday, (int)DayOfWeek.Saturday], 2m);
        PricingRule discountRule = PricingRule.CreateLengthOfStayDiscount(UnitId, 7, 10m);

        StayPriceBreakdown breakdown = PricingCalculator.ResolveStayTotal(
            Usd(100m), checkIn, checkOut, [overrideRule, weekendRule, discountRule]);

        // Nights: Mon100 Tue100 Wed100 Thu100 Fri200 Sat(override)500 Sun100 = 1200 subtotal
        Assert.Equal(Usd(1200m), breakdown.Subtotal);
        Assert.Equal(Usd(120m), breakdown.LengthOfStayDiscountAmount);
        Assert.Equal(Usd(1080m), breakdown.Total); // 1200 * 0.9
    }

    [Fact]
    public void ResolveStayTotal_ShouldRoundPerNight_ForThreeDecimalCurrency()
    {
        // KWD has 3 decimal places (docs/adr/0015) - a base price that
        // doesn't divide evenly under a multiplier is exactly where per-
        // night rounding (vs. rounding once at the end) can produce a
        // different, and correct, result: 33.333 * 3 nights = 99.999, not
        // 100.00 - each night is independently a real payable amount.
        DateOnly checkIn = new DateOnly(2026, 1, 1);
        DateOnly checkOut = new DateOnly(2026, 1, 4); // 3 nights
        Money basePrice = Money.Of(33.3333m, Currency.KWD);

        StayPriceBreakdown breakdown = PricingCalculator.ResolveStayTotal(basePrice, checkIn, checkOut, []);

        Assert.Equal(Money.Of(99.999m, Currency.KWD), breakdown.Total);
    }
}
