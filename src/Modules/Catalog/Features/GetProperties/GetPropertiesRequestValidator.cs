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
    }
}
