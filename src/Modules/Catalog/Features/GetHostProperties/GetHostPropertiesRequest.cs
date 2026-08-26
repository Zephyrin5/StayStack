using BuildingBlocks.Pagination;
using Catalog.Features.GetProperties;
using Mediator;
namespace Catalog.Features.GetHostProperties;

// Separate from GetMyPropertiesRequest on purpose - see docs/adr/0007 and
// its docs/adr/0013 extension. HostId here is trusted, Administrator-only
// input naming a target host, never derived from the caller's own token
// the way GetMyPropertiesRequest's is.
public record GetHostPropertiesRequest : IRequest<PagedResponse<PropertySummary>>
{
    public Guid HostId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
