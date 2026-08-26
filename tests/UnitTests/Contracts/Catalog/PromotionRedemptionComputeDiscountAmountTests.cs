using Catalog.Entities;
using Catalog.Enums;
using SeedWork.Enums;
using PromotionRedemption = Catalog.Contracts.PromotionRedemption;
namespace UnitTests.Contracts.Catalog;

// Exercises PromotionRedemption's internal ComputeDiscountAmount helper
// directly (internal, not private, specifically for this - see its own doc
// comment) rather than only indirectly through a full RedeemAsync call,
// which would need a real database. Namespace nests "Catalog" one level
// under UnitTests.Contracts rather than as the leading segment - see
// UnitTests.Domain.Catalog (PricingCalculatorTests) for the same safe
// pattern and why a leading "Identity"-style segment caused a real
// namespace-shadowing bug earlier in this codebase.
public class PromotionRedemptionComputeDiscountAmountTests
{
    [Fact]
    public void ComputeDiscountAmount_ShouldApplyPercentage_OfSubtotal()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 20m, null, null, null);

        decimal discount = PromotionRedemption.ComputeDiscountAmount(promotion, 200m);

        Assert.Equal(40m, discount);
    }

    [Fact]
    public void ComputeDiscountAmount_ShouldClampPercentage_AtSubtotal()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 100m, null, null, null);

        decimal discount = PromotionRedemption.ComputeDiscountAmount(promotion, 50m);

        Assert.Equal(50m, discount);
    }

    [Fact]
    public void ComputeDiscountAmount_ShouldReturnFixedAmount_WhenBelowSubtotal()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.FixedAmount, 15m, Currency.KWD, null, null);

        decimal discount = PromotionRedemption.ComputeDiscountAmount(promotion, 200m);

        Assert.Equal(15m, discount);
    }

    [Fact]
    public void ComputeDiscountAmount_ShouldClampFixedAmount_AtSubtotal()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.FixedAmount, 500m, Currency.KWD, null, null);

        decimal discount = PromotionRedemption.ComputeDiscountAmount(promotion, 200m);

        Assert.Equal(200m, discount);
    }

    [Fact]
    public void ComputeDiscountAmount_ShouldNeverBeNegative_WhenSubtotalIsZero()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.FixedAmount, 15m, Currency.KWD, null, null);

        decimal discount = PromotionRedemption.ComputeDiscountAmount(promotion, 0m);

        Assert.Equal(0m, discount);
    }
}
