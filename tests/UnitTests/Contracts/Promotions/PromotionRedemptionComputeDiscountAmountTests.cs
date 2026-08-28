using Promotions.Entities;
using Promotions.Enums;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using PromotionRedemption = Promotions.Contracts.PromotionRedemption;
namespace UnitTests.Contracts.Promotions;

// Exercises PromotionRedemption's internal ComputeDiscountAmount helper
// directly (internal, not private, specifically for this - see its own doc
// comment) rather than only indirectly through a full RedeemAsync call,
// which would need a real database. Namespace nests "Promotions" one level
// under UnitTests.Contracts rather than as the leading segment - see
// UnitTests.Domain.Catalog (PricingCalculatorTests) for the same safe
// pattern and why a leading "Identity"-style segment caused a real
// namespace-shadowing bug earlier in this codebase.
public class PromotionRedemptionComputeDiscountAmountTests
{
    private static Money Kwd(decimal amount) => Money.Of(amount, Currency.KWD);

    [Fact]
    public void ComputeDiscountAmount_ShouldApplyPercentage_OfSubtotal()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 20m, null, null, null);

        Money discount = PromotionRedemption.ComputeDiscountAmount(promotion, Kwd(200m));

        Assert.Equal(Kwd(40m), discount);
    }

    [Fact]
    public void ComputeDiscountAmount_ShouldClampPercentage_AtSubtotal()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.Percentage, 100m, null, null, null);

        Money discount = PromotionRedemption.ComputeDiscountAmount(promotion, Kwd(50m));

        Assert.Equal(Kwd(50m), discount);
    }

    [Fact]
    public void ComputeDiscountAmount_ShouldReturnFixedAmount_WhenBelowSubtotal()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.FixedAmount, 15m, Currency.KWD, null, null);

        Money discount = PromotionRedemption.ComputeDiscountAmount(promotion, Kwd(200m));

        Assert.Equal(Kwd(15m), discount);
    }

    [Fact]
    public void ComputeDiscountAmount_ShouldClampFixedAmount_AtSubtotal()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.FixedAmount, 500m, Currency.KWD, null, null);

        Money discount = PromotionRedemption.ComputeDiscountAmount(promotion, Kwd(200m));

        Assert.Equal(Kwd(200m), discount);
    }

    [Fact]
    public void ComputeDiscountAmount_ShouldNeverBeNegative_WhenSubtotalIsZero()
    {
        Promotion promotion = Promotion.CreateHostPromotion(
            Guid.NewGuid(), "CODE", PromotionDiscountType.FixedAmount, 15m, Currency.KWD, null, null);

        Money discount = PromotionRedemption.ComputeDiscountAmount(promotion, Kwd(0m));

        Assert.Equal(Kwd(0m), discount);
    }
}
