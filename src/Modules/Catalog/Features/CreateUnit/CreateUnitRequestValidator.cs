using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.CreateUnit;

public sealed class CreateUnitRequestValidator : Validator<CreateUnitRequest>
{
    public CreateUnitRequestValidator()
    {
        RuleFor(x => x.PropertyId).NotEmpty();
        RuleFor(x => x.Name).NotEmpty().WithMessage("At least one localized name value is required.");
        RuleFor(x => x.MaxOccupancy).GreaterThan(0);
        RuleFor(x => x.BasePrice).GreaterThan(0);
        RuleFor(x => x.Currency).IsInEnum();

        // Whether PropertyId refers to a real Property is a database
        // concern, checked in the handler - Property lives in this same
        // module/DbContext, so no cross-module contract is needed for it
        // the way HostId needed one in CreateProperty.
    }
}
