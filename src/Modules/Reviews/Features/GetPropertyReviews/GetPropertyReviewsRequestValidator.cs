using BuildingBlocks.Pagination;
using FastEndpoints;
using FluentValidation;
namespace Reviews.Features.GetPropertyReviews;

public sealed class GetPropertyReviewsRequestValidator : Validator<GetPropertyReviewsRequest>
{
    public GetPropertyReviewsRequestValidator()
    {
        RuleFor(x => x.PropertyId).NotEmpty();
        RuleFor(x => x.Page).GreaterThanOrEqualTo(1);
        RuleFor(x => x.PageSize).InclusiveBetween(1, PaginationExtensions.MaxPageSize);
    }
}
