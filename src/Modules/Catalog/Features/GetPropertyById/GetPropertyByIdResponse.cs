using SeedWork.Enums;
namespace Catalog.Features.GetPropertyById;

public record GetPropertyByIdResponse
{
    public Guid Id { get; init; }
    public Guid HostId { get; init; }
    public PropertyType PropertyType { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public string? City { get; init; }
    public List<UnitSummary> Units { get; init; } = [];
}

public record UnitSummary
{
    public Guid Id { get; init; }
    public UnitType UnitType { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public int MaxOccupancy { get; init; }
    public decimal BasePrice { get; init; }
    public Currency Currency { get; init; }
}
