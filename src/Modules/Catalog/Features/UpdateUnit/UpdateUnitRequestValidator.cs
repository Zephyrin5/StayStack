using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.UpdateUnit;

public sealed class UpdateUnitRequestValidator : Validator<UpdateUnitRequest>
{
    public UpdateUnitRequestValidator()
    {
        RuleFor(x => x.UnitId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().WithMessage("At least one localized name value is required.");
        RuleFor(x => x.MaxOccupancy).GreaterThan(0);
        RuleFor(x => x.BasePrice).GreaterThan(0);
        RuleFor(x => x.Currency).IsInEnum();
    }
}
