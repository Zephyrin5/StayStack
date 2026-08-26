using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.GetHostProperties;

public sealed class GetHostPropertiesRequestValidator : Validator<GetHostPropertiesRequest>
{
    public GetHostPropertiesRequestValidator()
    {
        RuleFor(x => x.HostId).NotEmpty();
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);
    }
}
