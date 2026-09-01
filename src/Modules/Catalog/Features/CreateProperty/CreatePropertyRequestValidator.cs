using BuildingBlocks.Time;
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

        // Rejected here rather than at the domain guard so the caller gets a
        // field-level 400. PropertyTimeZone.IsValid wraps
        // TimeZoneInfo.TryFindSystemTimeZoneById - never the throwing
        // FindSystemTimeZoneById, whose TimeZoneNotFoundException would
        // escape GlobalExceptionHandler's ArgumentException arm as a 500.
        RuleFor(x => x.TimeZoneId)
            .NotEmpty()
            .Must(PropertyTimeZone.IsValid)
            .WithMessage("'{PropertyValue}' is not a recognised IANA time zone identifier (for example 'Asia/Kuwait').");
    }
}
