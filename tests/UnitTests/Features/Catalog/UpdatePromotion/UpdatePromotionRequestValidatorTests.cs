using Catalog.Features.UpdatePromotion;
using FluentValidation.TestHelper;
using SeedWork.Enums;
namespace UnitTests.Features.Catalog.UpdatePromotion;

public class UpdatePromotionRequestValidatorTests
{
    private readonly UpdatePromotionRequestValidator _sut = new UpdatePromotionRequestValidator();

    private static UpdatePromotionRequest CreateValidRequest()
    {
        return new UpdatePromotionRequest
        {
            PromotionId = Guid.NewGuid(),
            DiscountValue = 10m,
            Currency = Currency.KWD,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            MaxRedemptions = 100
        };
    }

    [Fact]
    public void Validate_ShouldNotHaveErrors_WhenRequestIsValid()
    {
        var result = _sut.TestValidate(CreateValidRequest());

        result.ShouldNotHaveAnyValidationErrors();
    }

    [Fact]
    public void Validate_ShouldHaveError_ForPromotionId_WhenEmpty()
    {
        UpdatePromotionRequest request = CreateValidRequest() with { PromotionId = Guid.Empty };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.PromotionId);
    }

    [Fact]
    public void Validate_ShouldHaveError_ForDiscountValue_WhenNotPositive()
    {
        UpdatePromotionRequest request = CreateValidRequest() with { DiscountValue = 0m };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.DiscountValue);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Validate_ShouldHaveError_ForMaxRedemptions_WhenNotPositive(int maxRedemptions)
    {
        UpdatePromotionRequest request = CreateValidRequest() with { MaxRedemptions = maxRedemptions };

        var result = _sut.TestValidate(request);

        result.ShouldHaveValidationErrorFor(x => x.MaxRedemptions);
    }

    [Fact]
    public void Validate_ShouldNotHaveError_ForMaxRedemptions_WhenNull()
    {
        UpdatePromotionRequest request = CreateValidRequest() with { MaxRedemptions = null };

        var result = _sut.TestValidate(request);

        result.ShouldNotHaveValidationErrorFor(x => x.MaxRedemptions);
    }
}
