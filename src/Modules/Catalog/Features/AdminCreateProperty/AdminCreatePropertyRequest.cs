using Catalog.Enums;
using Catalog.Features.CreateProperty;
using Mediator;
namespace Catalog.Features.AdminCreateProperty;

public record AdminCreatePropertyRequest : IRequest<CreatePropertyResponse>
{
    // Bound from the route (/api/hosts/{HostId}/properties), not the body -
    // making "you're acting on this host's behalf" visible in the URL
    // itself, rather than one more field sitting in a payload.
    public Guid HostId { get; init; }

    public PropertyType PropertyType { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public string? City { get; init; }
}
