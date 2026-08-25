using BuildingBlocks.Pagination;
using Catalog.Enums;
using Mediator;
namespace Catalog.Features.GetProperties;

// Public/anonymous (see GetPropertiesEndpoint) - deliberately has no HostId
// filter. That used to exist here for GetMyPropertiesEndpoint to reuse, but
// since this request binds straight from an anonymous caller's query
// string, it made "list properties for host X" reachable by anyone who
// guessed a host id, not derived from an authenticated caller's own token
// the way GetMyPropertiesRequest's handler resolves it instead. See
// docs/adr/0007 for why these stay two separate requests rather than one
// shared shape.
public record GetPropertiesRequest : IRequest<PagedResponse<PropertySummary>>
{
    public string? City { get; init; }
    public PropertyType? PropertyType { get; init; }

    // A pair, not two independent filters - both or neither. See the
    // validator for why, and GetPropertiesHandler for why capacity and
    // availability are both resolved against the same candidate unit
    // rather than as separate property-level checks.
    public DateOnly? CheckIn { get; init; }
    public DateOnly? CheckOut { get; init; }
    public int? Guests { get; init; }

    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
