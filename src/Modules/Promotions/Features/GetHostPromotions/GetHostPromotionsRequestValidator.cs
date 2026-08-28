using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Promotions.Features.GetHostPromotions;

public sealed class GetHostPromotionsRequestValidator : Validator<GetHostPromotionsRequest>
{
    public GetHostPromotionsRequestValidator()
    {
        RuleFor(x => x.HostId).NotEmpty();
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);
    }
}
