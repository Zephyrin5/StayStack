using Catalog.Enums;
namespace Catalog.Features.GetProperties;

public record PropertySummary
{
    public Guid Id { get; init; }
    public Guid HostId { get; init; }
    public PropertyType PropertyType { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public string? City { get; init; }
    public string TimeZoneId { get; init; } = string.Empty;
}
