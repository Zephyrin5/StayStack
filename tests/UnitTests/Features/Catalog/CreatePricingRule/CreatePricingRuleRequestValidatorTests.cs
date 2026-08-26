using Catalog.Enums;
using Catalog.Features.CreatePricingRule;
using FluentValidation.TestHelper;
namespace UnitTests.Features.Catalog.CreatePricingRule;

public class CreatePricingRuleRequestValidatorTests
{
    private readonly CreatePricingRuleRequestValidator _sut = new CreatePricingRuleRequestValidator();

    private static CreatePricingRuleRequest CreateValidDateRangeOverrideRequest()
    {
        return new CreatePricingRuleRequest
        {
            UnitId = Guid.NewGuid(),
            RuleType = PricingRuleType.DateRangeOverride,
            StartDate = new DateOnly(2026, 12, 20),
            EndDate = new DateOnly(2026, 12, 31),
            OverridePrice = 250m
        };
    }

    private static CreatePricingRuleRequest CreateValidDayOfWeekMultiplierRequest()
    {
        return new CreatePricingRuleRequest
        {
            UnitId = Guid.NewGuid(),
            RuleType = PricingRuleType.DayOfWeekMultiplier,
            DaysOfWeek = [5, 6],
            Multiplier = 1.5m
        };
    }

    private static CreatePricingRuleRequest CreateValidLengthOfStayDiscountRequest()
    {
        return new CreatePricingRuleRequest
        {
            UnitId = Guid.NewGuid(),
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
        CreatePricingRuleRequest request = CreateValidDateRangeOverrideRequest() with { UnitId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.UnitId);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForRuleType_WhenNotAValidEnumMember()
    {
        CreatePricingRuleRequest request = CreateValidDateRangeOverrideRequest() with { RuleType = (PricingRuleType)99 };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.RuleType);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForStartDate_WhenMissing()
    {
        CreatePricingRuleRequest request = CreateValidDateRangeOverrideRequest() with { StartDate = null };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.StartDate);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForEndDate_WhenMissing()
    {
        CreatePricingRuleRequest request = CreateValidDateRangeOverrideRequest() with { EndDate = null };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.EndDate);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForEndDate_WhenNotAfterStartDate()
    {
        CreatePricingRuleRequest request = CreateValidDateRangeOverrideRequest() with
        {
            StartDate = new DateOnly(2026, 12, 20),
            EndDate = new DateOnly(2026, 12, 20)
        };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.EndDate);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForOverridePrice_WhenMissing()
    {
        CreatePricingRuleRequest request = CreateValidDateRangeOverrideRequest() with { OverridePrice = null };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.OverridePrice);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForOverridePrice_WhenNotPositive()
    {
        CreatePricingRuleRequest request = CreateValidDateRangeOverrideRequest() with { OverridePrice = 0m };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.OverridePrice);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForDaysOfWeek_WhenMissing()
    {
        CreatePricingRuleRequest request = CreateValidDayOfWeekMultiplierRequest() with { DaysOfWeek = null };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DaysOfWeek);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForDaysOfWeek_WhenEmpty()
    {
        CreatePricingRuleRequest request = CreateValidDayOfWeekMultiplierRequest() with { DaysOfWeek = [] };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DaysOfWeek);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForDaysOfWeek_WhenOutOfRange()
    {
        CreatePricingRuleRequest request = CreateValidDayOfWeekMultiplierRequest() with { DaysOfWeek = [7] };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DaysOfWeek);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForDaysOfWeek_WhenDuplicated()
    {
        CreatePricingRuleRequest request = CreateValidDayOfWeekMultiplierRequest() with { DaysOfWeek = [5, 5] };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DaysOfWeek);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForMultiplier_WhenMissing()
    {
        CreatePricingRuleRequest request = CreateValidDayOfWeekMultiplierRequest() with { Multiplier = null };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Multiplier);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForMinNights_WhenMissing()
    {
        CreatePricingRuleRequest request = CreateValidLengthOfStayDiscountRequest() with { MinNights = null };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.MinNights);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForDiscountPercent_WhenMissing()
    {
        CreatePricingRuleRequest request = CreateValidLengthOfStayDiscountRequest() with { DiscountPercent = null };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DiscountPercent);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public void Validate_ShouldHaveError_ForDiscountPercent_WhenOutOfRange(decimal discountPercent)
    {
        CreatePricingRuleRequest request = CreateValidLengthOfStayDiscountRequest() with { DiscountPercent = discountPercent };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DiscountPercent);
    }
}
