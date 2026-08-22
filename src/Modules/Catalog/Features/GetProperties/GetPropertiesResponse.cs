using SeedWork.Enums;
namespace Catalog.Features.GetProperties;

public record GetPropertiesResponse
{
    public List<PropertySummary> Properties { get; init; } = [];
}

public record PropertySummary
{
    public Guid Id { get; init; }
    public Guid HostId { get; init; }
    public PropertyType PropertyType { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public string? City { get; init; }
}
