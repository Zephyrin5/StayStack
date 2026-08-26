using Catalog.Enums;
using Catalog.Features.UpdatePricingRule;
using FluentValidation.TestHelper;
namespace UnitTests.Features.Catalog.UpdatePricingRule;

public class UpdatePricingRuleRequestValidatorTests
{
    private readonly UpdatePricingRuleRequestValidator _sut = new UpdatePricingRuleRequestValidator();

    private static UpdatePricingRuleRequest CreateValidDateRangeOverrideRequest()
    {
        return new UpdatePricingRuleRequest
        {
            UnitId = Guid.NewGuid(),
            PricingRuleId = Guid.NewGuid(),
            RuleType = PricingRuleType.DateRangeOverride,
            StartDate = new DateOnly(2026, 12, 20),
            EndDate = new DateOnly(2026, 12, 31),
            OverridePrice = 250m
        };
    }

    private static UpdatePricingRuleRequest CreateValidDayOfWeekMultiplierRequest()
    {
        return new UpdatePricingRuleRequest
        {
            UnitId = Guid.NewGuid(),
            PricingRuleId = Guid.NewGuid(),
            RuleType = PricingRuleType.DayOfWeekMultiplier,
            DaysOfWeek = [5, 6],
            Multiplier = 1.5m
        };
    }

    private static UpdatePricingRuleRequest CreateValidLengthOfStayDiscountRequest()
    {
        return new UpdatePricingRuleRequest
        {
            UnitId = Guid.NewGuid(),
            PricingRuleId = Guid.NewGuid(),
            RuleType = PricingRuleType.LengthOfStayDiscount,
            MinNights = 7,
            DiscountPercent = 10m
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenDateRangeOverrideIsValid()
    {
        var result = _sut.TestValidate(CreateValidDateRangeOverrideRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenDayOfWeekMultiplierIsValid()
    {
        var result = _sut.TestValidate(CreateValidDayOfWeekMultiplierRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenLengthOfStayDiscountIsValid()
    {
        var result = _sut.TestValidate(CreateValidLengthOfStayDiscountRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForUnitId_WhenEmpty()
    {
        UpdatePricingRuleRequest request = CreateValidDateRangeOverrideRequest() with { UnitId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.UnitId);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPricingRuleId_WhenEmpty()
    {
        UpdatePricingRuleRequest request = CreateValidDateRangeOverrideRequest() with { PricingRuleId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.PricingRuleId);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForRuleType_WhenNotAValidEnumMember()
    {
        UpdatePricingRuleRequest request = CreateValidDateRangeOverrideRequest() with { RuleType = (PricingRuleType)99 };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.RuleType);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForEndDate_WhenNotAfterStartDate()
    {
        UpdatePricingRuleRequest request = CreateValidDateRangeOverrideRequest() with
        {
            StartDate = new DateOnly(2026, 12, 20),
            EndDate = new DateOnly(2026, 12, 20)
        };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.EndDate);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForDaysOfWeek_WhenDuplicated()
    {
        UpdatePricingRuleRequest request = CreateValidDayOfWeekMultiplierRequest() with { DaysOfWeek = [5, 5] };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DaysOfWeek);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_ShouldHaveError_ForDiscountPercent_WhenOutOfRange(decimal discountPercent)
    {
        UpdatePricingRuleRequest request = CreateValidLengthOfStayDiscountRequest() with { DiscountPercent = discountPercent };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DiscountPercent);
    }
}
