namespace Catalog.Contracts;

/// <summary>
///     Lets Bookings resolve a unit's price/currency without ever
///     referencing Catalog's own entities or AppCatalogDbContext directly -
///     same boundary reasoning as Hosts.Contracts.IHostLookup.
/// </summary>
public interface IUnitLookup
{
    Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken);
}

public record UnitSummary
{
    public Guid Id { get; init; }
    public int MaxOccupancy { get; init; }
    public decimal BasePrice { get; init; }
    public string Currency { get; init; } = string.Empty;
}
