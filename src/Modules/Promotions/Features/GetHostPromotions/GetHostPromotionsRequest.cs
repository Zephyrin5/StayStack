using BuildingBlocks.Pagination;
using Mediator;
using Promotions.Features;
namespace Promotions.Features.GetHostPromotions;

// Separate from ListMyPromotionsRequest on purpose - see docs/adr/0007 and
// its docs/adr/0013 extension. HostId here is trusted, Administrator-only
// input naming a target host, never derived from the caller's own token
// the way ListMyPromotionsRequest's is.
public record GetHostPromotionsRequest : IRequest<PagedResponse<PromotionSummary>>
{
    public Guid HostId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
