using SeedWork.Enums;
namespace Catalog.Contracts;

/// <summary>
///     Lets Bookings resolve a unit's price/currency without ever
///     referencing Catalog's own entities or AppCatalogDbContext directly -
///     same boundary reasoning as Hosts.Contracts.IHostLookup.
/// </summary>
public interface IUnitLookup
{
    Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken);

    /// <summary>
    ///     Batch counterpart to GetUnitAsync - one round trip for many
    ///     units instead of one call per id. Missing ids are simply absent
    ///     from the result rather than represented as a null entry, so
    ///     callers use TryGetValue/indexer-with-fallback the same way they
    ///     already handle a null single GetUnitAsync result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, UnitSummary>> GetUnitsAsync(IEnumerable<Guid> unitIds, CancellationToken cancellationToken);
}

public record UnitSummary
{
    public Guid Id { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public int MaxOccupancy { get; init; }
    public decimal BasePrice { get; init; }
    public Currency Currency { get; init; }
}
