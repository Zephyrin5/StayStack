using Catalog.Enums;
using Mediator;
namespace Catalog.Features.GetProperties;

// Public/anonymous (see GetPropertiesEndpoint) - deliberately has no HostId
// filter. That used to exist here for GetMyPropertiesEndpoint to reuse,
// but since this request binds straight from an anonymous caller's query
// string, it made "list properties for host X" reachable by anyone who
// guessed a host id, not just derived from an authenticated caller's own
// token the way GetMyPropertiesRequest's handler resolves it. See
// GetMyProperties for the host-scoped equivalent.
public record GetPropertiesRequest : IRequest<GetPropertiesResponse>
{
    public string? City { get; init; }
    public PropertyType? PropertyType { get; init; }
}
