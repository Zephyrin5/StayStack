using BuildingBlocks.Pagination;
using Mediator;
namespace Reviews.Features.GetPropertyReviews;

// Public - no authentication required.
public record GetPropertyReviewsRequest : IRequest<GetPropertyReviewsResponse>
{
    public Guid PropertyId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
