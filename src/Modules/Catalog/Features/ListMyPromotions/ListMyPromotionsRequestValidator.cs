using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Catalog.Features.ListMyPromotions;

public sealed class ListMyPromotionsRequestValidator : Validator<ListMyPromotionsRequest>
{
    public ListMyPromotionsRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);
    }
}
