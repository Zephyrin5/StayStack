using BuildingBlocks.Pagination;
using Mediator;
using Reviews.Features.GetPropertyReviews;
namespace Reviews.Features.GetHostStayReviews;

// No HostId field - resolved server-side via IHostAuthorization, same
// pattern as GetMyPropertiesRequest/ListMyPromotionsRequest. Reuses
// StayReviewSummary - same item shape GetPropertyReviews already returns,
// no trust-boundary concern on a response type.
public record GetHostStayReviewsRequest : IRequest<PagedResponse<StayReviewSummary>>
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
