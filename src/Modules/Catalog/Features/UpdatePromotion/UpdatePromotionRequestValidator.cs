using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.UpdatePromotion;

public sealed class UpdatePromotionRequestValidator : Validator<UpdatePromotionRequest>
{
    public UpdatePromotionRequestValidator()
    {
        RuleFor(x => x.PromotionId).NotEmpty();
        RuleFor(x => x.DiscountValue).GreaterThan(0);
        RuleFor(x => x.MaxRedemptions).GreaterThan(0).When(x => x.MaxRedemptions is not null);

        // Whether PromotionId refers to a real promotion, ownership, and
        // whether DiscountValue/Currency are valid for the promotion's
        // (unchangeable) DiscountType are all handler/entity concerns -
        // same convention as UpdatePricingRuleRequestValidator.
    }
}
