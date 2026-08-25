using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.UpdateProperty;

public sealed class UpdatePropertyRequestValidator : Validator<UpdatePropertyRequest>
{
    public UpdatePropertyRequestValidator()
    {
        RuleFor(x => x.PropertyId).NotEmpty();
        RuleFor(x => x.PropertyType).IsInEnum();
        RuleFor(x => x.Name).NotEmpty().WithMessage("At least one localized name value is required.");
        RuleFor(x => x.City).MaximumLength(100);
    }
}
