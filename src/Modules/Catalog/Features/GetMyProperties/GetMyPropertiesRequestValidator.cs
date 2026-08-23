using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.GetMyProperties;

public sealed class GetMyPropertiesRequestValidator : Validator<GetMyPropertiesRequest>
{
    public GetMyPropertiesRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);
    }
}
