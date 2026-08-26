using Catalog.Enums;
using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.UpdatePricingRule;

public sealed class UpdatePricingRuleRequestValidator : Validator<UpdatePricingRuleRequest>
{
    public UpdatePricingRuleRequestValidator()
    {
        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.PricingRuleId).NotEmpty();
        RuleFor(x => x.RuleType).IsInEnum();

        When(x => x.RuleType == PricingRuleType.DateRangeOverride, () =>
        {
            RuleFor(x => x.StartDate).NotNull();
            RuleFor(x => x.EndDate).NotNull();
            RuleFor(x => x.EndDate)
                .GreaterThan(x => x.StartDate!.Value)
                .When(x => x.StartDate is not null && x.EndDate is not null);
            RuleFor(x => x.OverridePrice).NotNull().GreaterThan(0);
        });

        When(x => x.RuleType == PricingRuleType.DayOfWeekMultiplier, () =>
        {
            // Must's predicate has to tolerate null itself - FluentValidation
            // runs every validator in a chain by default (CascadeMode.Continue),
            // so NotNull() failing doesn't skip the Must() call that follows it.
            RuleFor(x => x.DaysOfWeek)
                .NotNull()
                .Must(d => d is not null && d.Length > 0 && d.All(v => v is >= 0 and <= 6) && d.Distinct().Count() == d.Length)
                .WithMessage("DaysOfWeek must be a non-empty list of distinct values between 0 and 6.");
            RuleFor(x => x.Multiplier).NotNull().GreaterThan(0);
        });

        When(x => x.RuleType == PricingRuleType.LengthOfStayDiscount, () =>
        {
            RuleFor(x => x.MinNights).NotNull().GreaterThan(0);
            RuleFor(x => x.DiscountPercent).NotNull().InclusiveBetween(0.01m, 100m);
        });

        // Whether PricingRuleId refers to a real rule, whether it belongs
        // to UnitId, whether RuleType matches the existing rule, and
        // whether the new values overlap another rule are all database
        // concerns checked in the handler - same convention as
        // CreatePricingRuleRequestValidator/CreateUnitRequestValidator.
    }
}
