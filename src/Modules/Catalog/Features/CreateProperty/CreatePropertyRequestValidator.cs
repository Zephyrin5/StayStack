using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.CreateProperty;

public sealed class CreatePropertyRequestValidator : Validator<CreatePropertyRequest>
{
    public CreatePropertyRequestValidator()
    {
        RuleFor(x => x.PropertyType).IsInEnum();
        RuleFor(x => x.Name).NotEmpty().WithMessage("At least one localized name value is required.");
        RuleFor(x => x.City).MaximumLength(100);
    }
}
