using Catalog.Enums;
using Mediator;
namespace Catalog.Features.UpdateProperty;

public record UpdatePropertyRequest : IRequest<UpdatePropertyResponse>
{
    public Guid PropertyId { get; init; }
    public PropertyType PropertyType { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public string? City { get; init; }
}
