using Promotions.Entities;
using Promotions.Enums;
using SeedWork.Enums;
namespace UnitTests.Entities;

public class PromotionTests
{
    [Fact]
    public void CreateHostPromotion_ShouldSetAllProperties_WhenInputIsValid()
    {
        Guid hostId = Guid.NewGuid();
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddDays(30);

        Promotion promotion = Promotion.CreateHostPromotion(
            hostId, " summer26 ", PromotionDiscountType.Percentage, 10m, null, expiresAt, 100);

        Assert.NotEqual(Guid.Empty, promotion.Id);
        Assert.Equal("SUMMER26", promotion.Code);
        Assert.Equal(hostId, promotion.HostId);
        Assert.Equal(PromotionDiscountType.Percentage, promotion.DiscountType);
        Assert.Equal(10m, promotion.DiscountValue);
        Assert.Null(promotion.Currency);
        Assert.Equal(expiresAt, promotion.ExpiresAt);
        Assert.Equal(100, promotion.MaxRedemptions);
        Assert.Equal(0, promotion.RedemptionCount);
    }

    [Fact]
    public void CreateHostPromotion_ShouldThrow_WhenHostIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => Promotion.CreateHostPromotion(
            Guid.Empty, "CODE", PromotionDiscountType.Percentage, 10m, null, null, null));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void CreateHostPromotion_ShouldThrow_WhenCodeIsNullOrWhitespace(string? code)
    {
        Assert.ThrowsAny<ArgumentException>(() => Promotion.CreateHostPromotion(
            Guid.NewGuid(), code!, PromotionDiscountType.Percentage, 10m, null, null, null));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(100.01)]
    public void CreateHostPromotion_ShouldThrow_WhenPercentageDiscountValueIsOutOfRange(double discountValue)
    {
        Assert.ThrowsAny<ArgumentException>(() => Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, (decimal)discountValue, null, null, null));
    }

    [Fact]
    public void CreateHostPromotion_ShouldAllowPercentageDiscountValueOfExactlyOneHundred()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 100m, null, null, null);

        Assert.Equal(100m, promotion.DiscountValue);
    }

    [Fact]
    public void CreateHostPromotion_ShouldThrow_WhenFixedAmountDiscountValueIsNotPositive()
    {
        Assert.ThrowsAny<ArgumentException>(() => Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.FixedAmount, 0m, Currency.KWD, null, null));
    }

    [Fact]
    public void CreateHostPromotion_ShouldThrow_WhenFixedAmountHasNoCurrency()
    {
        Assert.Throws<ArgumentException>(() => Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.FixedAmount, 10m, null, null, null));
    }

    [Fact]
    public void CreateHostPromotion_ShouldAllowFixedAmountWithCurrency()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.FixedAmount, 10m, Currency.KWD, null, null);

        Assert.Equal(Currency.KWD, promotion.Currency);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void CreateHostPromotion_ShouldThrow_WhenMaxRedemptionsIsNotPositive(int maxRedemptions)
    {
        Assert.ThrowsAny<ArgumentException>(() => Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 10m, null, null, maxRedemptions));
    }

    [Fact]
    public void CreateHostPromotion_ShouldAllowNullMaxRedemptions()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 10m, null, null, null);

        Assert.Null(promotion.MaxRedemptions);
    }

    [Fact]
    public void CreatePlatformPromotion_ShouldAllowNullHostId()
    {
        Promotion promotion = Promotion.CreatePlatformPromotion(
            "CODE", PromotionDiscountType.Percentage, 10m, null, null, null, null);

        Assert.Null(promotion.HostId);
    }

    [Fact]
    public void CreatePlatformPromotion_ShouldAllowExplicitHostId()
    {
        Guid hostId = Guid.NewGuid();

        Promotion promotion = Promotion.CreatePlatformPromotion(
            "CODE", PromotionDiscountType.Percentage, 10m, null, null, null, hostId);

        Assert.Equal(hostId, promotion.HostId);
    }

    [Fact]
    public void CreatePlatformPromotion_ShouldThrow_WhenFixedAmountHasNoCurrency()
    {
        Assert.Throws<ArgumentException>(() => Promotion.CreatePlatformPromotion(
            "CODE", PromotionDiscountType.FixedAmount, 10m, null, null, null, null));
    }

    [Fact]
    public void SetDiscountValue_ShouldThrow_WhenPercentageValueIsOutOfRange()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 10m, null, null, null);

        Assert.ThrowsAny<ArgumentException>(() => promotion.SetDiscountValue(0m));
    }

    [Fact]
    public void SetDiscountValue_ShouldUpdateValue_WhenValid()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 10m, null, null, null);

        promotion.SetDiscountValue(20m);

        Assert.Equal(20m, promotion.DiscountValue);
    }

    [Fact]
    public void SetCurrency_ShouldThrow_WhenFixedAmountAndCurrencyIsNull()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.FixedAmount, 10m, Currency.KWD, null, null);

        Assert.Throws<ArgumentException>(() => promotion.SetCurrency(null));
    }

    [Fact]
    public void SetExpiresAt_ShouldUpdateValue()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 10m, null, null, null);
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.AddDays(5);

        promotion.SetExpiresAt(expiresAt);

        Assert.Equal(expiresAt, promotion.ExpiresAt);
    }

    [Fact]
    public void SetMaxRedemptions_ShouldThrow_WhenNotPositive()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 10m, null, null, null);

        Assert.ThrowsAny<ArgumentException>(() => promotion.SetMaxRedemptions(0));
    }

    [Fact]
    public void SetMaxRedemptions_ShouldAllowNull()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 10m, null, null, 5);

        promotion.SetMaxRedemptions(null);

        Assert.Null(promotion.MaxRedemptions);
    }
}
