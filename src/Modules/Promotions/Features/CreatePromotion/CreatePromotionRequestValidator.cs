using FastEndpoints;
using FluentValidation;
using Promotions.Enums;
namespace Promotions.Features.CreatePromotion;

public sealed class CreatePromotionRequestValidator : Validator<CreatePromotionRequest>
{
    public CreatePromotionRequestValidator()
    {
        RuleFor(x => x.Code).NotEmpty().MaximumLength(30);
        RuleFor(x => x.DiscountType).IsInEnum();

        When(x => x.DiscountType == PromotionDiscountType.Percentage, () =>
        {
            RuleFor(x => x.DiscountValue).InclusiveBetween(0.01m, 100m);
        });

        When(x => x.DiscountType == PromotionDiscountType.FixedAmount, () =>
        {
            RuleFor(x => x.DiscountValue).GreaterThan(0);
            RuleFor(x => x.Currency).NotNull();
        });

        RuleFor(x => x.MaxRedemptions).GreaterThan(0).When(x => x.MaxRedemptions is not null);

        // Whether Code is already taken is a database concern checked in
        // the handler (a unique-index violation on save) - same convention
        // as CreatePropertyRequestValidator/CreatePricingRuleRequestValidator.
    }
}
