using BuildingBlocks.Pagination;
using Catalog.Features.GetProperties;
using Mediator;
namespace Catalog.Features.GetMyProperties;

// No HostId field, still - GetMyPropertiesHandler resolves it itself via
// IHostAuthorization, the same pattern CreatePropertyHandler uses, so
// "whose properties" can only ever come from the caller's own token. Reuses
// PagedResponse<PropertySummary> - same item shape as the public browse
// endpoint, no trust-boundary concern on a response type.
public record GetMyPropertiesRequest : IRequest<PagedResponse<PropertySummary>>
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
