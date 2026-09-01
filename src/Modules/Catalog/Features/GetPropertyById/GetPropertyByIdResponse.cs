using Catalog.Enums;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace Catalog.Features.GetPropertyById;

public record GetPropertyByIdResponse
{
    public Guid Id { get; init; }
    public Guid HostId { get; init; }
    public PropertyType PropertyType { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public string? City { get; init; }

    // Exposed so clients can render this property's dates in the same zone
    // the server resolves them in (docs/adr/0018).
    public string TimeZoneId { get; init; } = string.Empty;
    public List<UnitSummary> Units { get; init; } = [];
}

public record UnitSummary
{
    public Guid Id { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public int MaxOccupancy { get; init; }
    public decimal BasePrice { get; init; }
    public Currency Currency { get; init; }

    // The unit's current cancellation terms - shown to a guest on the
    // property detail page and at the booking-confirm step, before they
    // ever commit to a stay.
    public List<CancellationTier> CancellationTiers { get; init; } = [];
}
