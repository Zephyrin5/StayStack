using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.AdminCreateProperty;

public sealed class AdminCreatePropertyRequestValidator : Validator<AdminCreatePropertyRequest>
{
    public AdminCreatePropertyRequestValidator()
    {
        RuleFor(x => x.HostId).NotEmpty();
        RuleFor(x => x.PropertyType).IsInEnum();
        RuleFor(x => x.Name).NotEmpty().WithMessage("At least one localized name value is required.");
        RuleFor(x => x.City).MaximumLength(100);
    }
}
