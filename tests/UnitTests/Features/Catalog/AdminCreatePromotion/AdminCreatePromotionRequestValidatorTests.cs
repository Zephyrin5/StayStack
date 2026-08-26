using Catalog.Enums;
using Catalog.Features.AdminCreatePromotion;
using FluentValidation.TestHelper;
using SeedWork.Enums;
namespace UnitTests.Features.Catalog.AdminCreatePromotion;

public class AdminCreatePromotionRequestValidatorTests
{
    private readonly AdminCreatePromotionRequestValidator _sut = new AdminCreatePromotionRequestValidator();

    private static AdminCreatePromotionRequest CreateValidPlatformWideRequest()
    {
        return new AdminCreatePromotionRequest
        {
            HostId = null,
            Code = "PLATFORM10",
            DiscountType = PromotionDiscountType.Percentage,
            DiscountValue = 10m
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenPlatformWideIsValid()
    {
        var result = _sut.TestValidate(CreateValidPlatformWideRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenHostScopedIsValid()
    {
        AdminCreatePromotionRequest request = CreateValidPlatformWideRequest() with { HostId = Guid.NewGuid() };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCode_WhenEmpty()
    {
        AdminCreatePromotionRequest request = CreateValidPlatformWideRequest() with { Code = "" };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Code);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForCurrency_WhenFixedAmountAndMissing()
    {
        AdminCreatePromotionRequest request = CreateValidPlatformWideRequest() with
        {
            DiscountType = PromotionDiscountType.FixedAmount,
            DiscountValue = 10m,
            Currency = null
        };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.Currency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100.01)]
    public void Validate_ShouldHaveError_ForDiscountValue_WhenPercentageOutOfRange(double discountValue)
    {
        AdminCreatePromotionRequest request = CreateValidPlatformWideRequest() with { DiscountValue = (decimal)discountValue };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DiscountValue);
    }
}
