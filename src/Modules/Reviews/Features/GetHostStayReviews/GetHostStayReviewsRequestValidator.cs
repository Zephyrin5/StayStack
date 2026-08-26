using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Reviews.Features.GetHostStayReviews;

public sealed class GetHostStayReviewsRequestValidator : Validator<GetHostStayReviewsRequest>
{
    public GetHostStayReviewsRequestValidator()
    {
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);
    }
}
