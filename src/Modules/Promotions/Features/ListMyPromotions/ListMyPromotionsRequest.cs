using BuildingBlocks.Pagination;
using Mediator;
using Promotions.Features;
namespace Promotions.Features.ListMyPromotions;

// No HostId field - resolved server-side via IHostAuthorization, same
// pattern as GetMyPropertiesRequest.
public record ListMyPromotionsRequest : IRequest<PagedResponse<PromotionSummary>>
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
