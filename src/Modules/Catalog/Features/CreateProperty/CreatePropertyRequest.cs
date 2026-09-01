using Catalog.Enums;
using Mediator;
namespace Catalog.Features.CreateProperty;

// No HostId here, deliberately - this endpoint is Host-only, and HostId
// is derived server-side from the caller's token (see CreatePropertyHandler),
// never accepted as input. Admins creating a property on a host's behalf
// go through AdminCreateProperty instead, which takes HostId from the
// route, not a trusted-by-accident body field.
public record CreatePropertyRequest : IRequest<CreatePropertyResponse>
{
    public PropertyType PropertyType { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public string? City { get; init; }

    // Required - see Property.TimeZoneId. Every business date for this
    // property resolves in this zone, so there is no safe default.
    public string TimeZoneId { get; init; } = string.Empty;
}
