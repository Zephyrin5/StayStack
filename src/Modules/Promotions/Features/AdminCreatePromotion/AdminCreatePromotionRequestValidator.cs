using FastEndpoints;
using FluentValidation;
using Promotions.Enums;
namespace Promotions.Features.AdminCreatePromotion;

public sealed class AdminCreatePromotionRequestValidator : Validator<AdminCreatePromotionRequest>
{
    public AdminCreatePromotionRequestValidator()
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

        // Whether HostId (when set) names a real host, and whether Code is
        // already taken, are database concerns checked in the handler -
        // same convention as AdminCreatePropertyRequestValidator.
    }
}
