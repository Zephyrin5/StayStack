using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.GetProperties;

public sealed class GetPropertiesRequestValidator : Validator<GetPropertiesRequest>
{
    public GetPropertiesRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);

        RuleFor(x => x.Guests).GreaterThan(0).When(x => x.Guests is not null);

        // CheckIn/CheckOut are a pair - "available from some date onward,
        // no end" isn't a search a caller can mean, so requiring one
        // requires the other rather than silently ignoring a lone value.
        RuleFor(x => x.CheckOut)
            .NotNull().WithMessage("CheckOut is required when CheckIn is provided.")
            .When(x => x.CheckIn is not null);

        RuleFor(x => x.CheckIn)
            .NotNull().WithMessage("CheckIn is required when CheckOut is provided.")
            .When(x => x.CheckOut is not null);

        RuleFor(x => x.CheckOut)
            .GreaterThan(x => x.CheckIn!.Value)
            .When(x => x.CheckIn is not null && x.CheckOut is not null);
    }
}
