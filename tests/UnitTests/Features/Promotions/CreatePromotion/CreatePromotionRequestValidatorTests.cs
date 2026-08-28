using FluentValidation.TestHelper;
using Promotions.Enums;
using Promotions.Features.CreatePromotion;
using SeedWork.Enums;
namespace UnitTests.Features.Promotions.CreatePromotion;

public class CreatePromotionRequestValidatorTests
{
    private readonly CreatePromotionRequestValidator _sut = new CreatePromotionRequestValidator();

    private static CreatePromotionRequest CreateValidPercentageRequest()
    {
        return new CreatePromotionRequest
        {
            Code = "SUMMER26",
            DiscountType = PromotionDiscountType.Percentage,
            DiscountValue = 10m
        };
    }

    private static CreatePromotionRequest CreateValidFixedAmountRequest()
    {
        return new CreatePromotionRequest
        {
            Code = "FLAT10",
            DiscountType = PromotionDiscountType.FixedAmount,
            DiscountValue = 10m,
            Currency = Currency.KWD
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenPercentageIsValid()
    {
        var result = _sut.TestValidate(CreateValidPercentageRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenFixedAmountIsValid()
    {
        var result = _sut.TestValidate(CreateValidFixedAmountRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCode_WhenEmpty()
    {
        CreatePromotionRequest request = CreateValidPercentageRequest() with { Code = "" };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCode_WhenTooLong()
    {
        CreatePromotionRequest request = CreateValidPercentageRequest() with { Code = new string('A', 31) };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForDiscountType_WhenNotAValidEnumMember()
    {
        CreatePromotionRequest request = CreateValidPercentageRequest() with { DiscountType = (PromotionDiscountType)99 };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DiscountType);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100.01)]
    public void Validate_ShouldHaveError_ForDiscountValue_WhenPercentageOutOfRange(double discountValue)
    {
        CreatePromotionRequest request = CreateValidPercentageRequest() with { DiscountValue = (decimal)discountValue };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DiscountValue);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForDiscountValue_WhenFixedAmountIsNotPositive()
    {
        CreatePromotionRequest request = CreateValidFixedAmountRequest() with { DiscountValue = 0m };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DiscountValue);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCurrency_WhenFixedAmountAndMissing()
    {
        CreatePromotionRequest request = CreateValidFixedAmountRequest() with { Currency = null };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Currency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldHaveError_ForMaxRedemptions_WhenNotPositive(int maxRedemptions)
    {
        CreatePromotionRequest request = CreateValidPercentageRequest() with { MaxRedemptions = maxRedemptions };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.MaxRedemptions);
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForMaxRedemptions_WhenNull()
    {
        CreatePromotionRequest request = CreateValidPercentageRequest() with { MaxRedemptions = null };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.MaxRedemptions);
    }
}
