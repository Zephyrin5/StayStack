using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Contracts;

// internal, same reasoning as Hosts.Contracts' implementations - Bookings
// should only ever reach this through IUnitLookup, resolved via DI.
internal class UnitLookup(AppCatalogDbContext dbContext) : IUnitLookup
{
    public async Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken)
    {
        // Materialize first, map after - see docs/adr/0006.
        Unit? unit = await dbContext.Units.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == unitId, cancellationToken);

        return unit is null
            ? null
            : new UnitSummary
            {
                Id = unit.Id,
                Name = new Dictionary<string, string>(unit.Name.Values),
                MaxOccupancy = unit.MaxOccupancy,
                BasePrice = unit.BasePrice,
                Currency = unit.Currency
            };
    }

    public async Task<IReadOnlyDictionary<Guid, UnitSummary>> GetUnitsAsync(IEnumerable<Guid> unitIds, CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. unitIds];

        // Same materialize-first-map-after constraint as GetUnitAsync above.
        List<Unit> units = await dbContext.Units.AsNoTracking()
            .Where(u => ids.Contains(u.Id))
            .ToListAsync(cancellationToken);

        return units.ToDictionary(
            unit => unit.Id,
            unit => new UnitSummary
            {
                Id = unit.Id,
                Name = new Dictionary<string, string>(unit.Name.Values),
                MaxOccupancy = unit.MaxOccupancy,
                BasePrice = unit.BasePrice,
                Currency = unit.Currency
            });
    }

    public async Task<IReadOnlyList<Guid>> GetUnitIdsForHostAsync(Guid hostId, CancellationToken cancellationToken)
    {
        return await dbContext.Units.AsNoTracking()
            .Where(u => dbContext.Properties.Where(p => p.HostId == hostId).Select(p => p.Id).Contains(u.PropertyId))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
    }
}
