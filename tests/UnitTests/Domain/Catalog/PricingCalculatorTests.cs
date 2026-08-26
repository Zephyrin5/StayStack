using Catalog.Domain;
using Catalog.Entities;
namespace UnitTests.Domain.Catalog;

public class PricingCalculatorTests
{
    private static readonly Guid UnitId = Guid.NewGuid();

    [Fact]
    public void ResolveNightlyPrice_ShouldReturnBasePrice_WhenNoRules()
    {
        decimal price = PricingCalculator.ResolveNightlyPrice(100m, new DateOnly(2026, 1, 1), []);

        Assert.Equal(100m, price);
    }

    [Fact]
    public void ResolveNightlyPrice_ShouldReturnOverridePrice_WhenDateInsideActiveOverride()
    {
        DateOnly date = new DateOnly(2026, 12, 25);
        PricingRule rule = PricingRule.CreateDateRangeOverride(
            UnitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 31), 250m);

        decimal price = PricingCalculator.ResolveNightlyPrice(100m, date, [rule]);

        Assert.Equal(250m, price);
    }

    [Fact]
    public void ResolveNightlyPrice_ShouldIgnoreMultiplier_WhenOverrideAlsoApplies()
    {
        DateOnly date = new DateOnly(2026, 12, 25); // a Friday
        PricingRule overrideRule = PricingRule.CreateDateRangeOverride(
            UnitId, new DateOnly(2026, 12, 20), new DateOnly(2026, 12, 31), 250m);
        PricingRule multiplierRule = PricingRule.CreateDayOfWeekMultiplier(UnitId, [5, 6], 1.5m);

        decimal price = PricingCalculator.ResolveNightlyPrice(100m, date, [overrideRule, multiplierRule]);

        Assert.Equal(250m, price);
    }

    [Fact]
    public void ResolveNightlyPrice_ShouldApplyMultiplier_WhenWeekdayMatches()
    {
        DateOnly saturday = new DateOnly(2026, 8, 29);
        PricingRule rule = PricingRule.CreateDayOfWeekMultiplier(UnitId, [(int)DayOfWeek.Saturday], 1.5m);

        decimal price = PricingCalculator.ResolveNightlyPrice(100m, saturday, [rule]);

        Assert.Equal(150m, price);
    }

    [Fact]
    public void ResolveNightlyPrice_ShouldReturnBasePrice_WhenWeekdayDoesNotMatch()
    {
        DateOnly tuesday = new DateOnly(2026, 9, 1);
        PricingRule rule = PricingRule.CreateDayOfWeekMultiplier(UnitId, [(int)DayOfWeek.Saturday], 1.5m);

        decimal price = PricingCalculator.ResolveNightlyPrice(100m, tuesday, [rule]);

        Assert.Equal(100m, price);
    }

    [Fact]
    public void ResolveStayTotal_ShouldSumMixOfOverrideAndBaseNights()
    {
        // Aug 29 (Sat, overridden) + Aug 30/31 (base) = 200 + 100 + 100
        DateOnly checkIn = new DateOnly(2026, 8, 29);
        DateOnly checkOut = new DateOnly(2026, 9, 1);
        PricingRule overrideRule = PricingRule.CreateDateRangeOverride(
            UnitId, new DateOnly(2026, 8, 29), new DateOnly(2026, 8, 30), 200m);

        decimal total = PricingCalculator.ResolveStayTotal(100m, checkIn, checkOut, [overrideRule]);

        Assert.Equal(400m, total);
    }

    [Fact]
    public void ResolveStayTotal_ShouldApplyMultiplierOnlyOnMatchingNights()
    {
        // Week of Mon Aug 24 - Sun Aug 30 2026: Fri 28 + Sat 29 are weekend nights.
        DateOnly checkIn = new DateOnly(2026, 8, 24);
        DateOnly checkOut = new DateOnly(2026, 8, 31); // 7 nights
        PricingRule weekendRule = PricingRule.CreateDayOfWeekMultiplier(
            UnitId, [(int)DayOfWeek.Friday, (int)DayOfWeek.Saturday], 2m);

        decimal total = PricingCalculator.ResolveStayTotal(100m, checkIn, checkOut, [weekendRule]);

        // 5 base nights @ 100 + 2 weekend nights @ 200
        Assert.Equal(900m, total);
    }

    [Fact]
    public void ResolveStayTotal_ShouldNotApplyDiscount_WhenNightsBelowThreshold()
    {
        DateOnly checkIn = new DateOnly(2026, 1, 1);
        DateOnly checkOut = new DateOnly(2026, 1, 7); // 6 nights
        PricingRule discountRule = PricingRule.CreateLengthOfStayDiscount(UnitId, 7, 10m);

        decimal total = PricingCalculator.ResolveStayTotal(100m, checkIn, checkOut, [discountRule]);

        Assert.Equal(600m, total);
    }

    [Fact]
    public void ResolveStayTotal_ShouldApplyDiscount_WhenNightsMeetThresholdExactly()
    {
        DateOnly checkIn = new DateOnly(2026, 1, 1);
        DateOnly checkOut = new DateOnly(2026, 1, 8); // 7 nights
        PricingRule discountRule = PricingRule.CreateLengthOfStayDiscount(UnitId, 7, 10m);

        decimal total = PricingCalculator.ResolveStayTotal(100m, checkIn, checkOut, [discountRule]);

        Assert.Equal(630m, total); // 700 * 0.9
    }

    [Fact]
    public void ResolveStayTotal_ShouldApplyDiscount_WhenNightsExceedThreshold()
    {
        DateOnly checkIn = new DateOnly(2026, 1, 1);
        DateOnly checkOut = new DateOnly(2026, 1, 15); // 14 nights
        PricingRule discountRule = PricingRule.CreateLengthOfStayDiscount(UnitId, 7, 10m);

        decimal total = PricingCalculator.ResolveStayTotal(100m, checkIn, checkOut, [discountRule]);

        Assert.Equal(1260m, total); // 1400 * 0.9
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

        decimal total = PricingCalculator.ResolveStayTotal(
            100m, checkIn, checkOut, [overrideRule, weekendRule, discountRule]);

        // Nights: Mon100 Tue100 Wed100 Thu100 Fri200 Sat(override)500 Sun100 = 1200 subtotal
        Assert.Equal(1080m, total); // 1200 * 0.9
    }
}
