using Catalog.Domain;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Contracts;

// internal, same reasoning as Hosts.Contracts' implementations - Bookings
// should only ever reach this through IUnitLookup, resolved via DI.
internal class UnitLookup(AppCatalogDbContext dbContext) : IUnitLookup
{
    public async Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken)
    {
        // Materialize first, map after - see docs/adr/0006. Left-joined
        // with Properties, not inner - some integration tests seed a bare
        // Unit against a throwaway PropertyId with no matching Property
        // row. An inner join would silently drop those units from every
        // result; HostId just comes back default (Guid.Empty) for that
        // synthetic case instead - never a real production state, since
        // CreateUnitHandler always targets a real Property.
        var row = await (
            from unit in dbContext.Units.AsNoTracking()
            where unit.Id == unitId
            join property in dbContext.Properties.AsNoTracking() on unit.PropertyId equals property.Id into properties
            from property in properties.DefaultIfEmpty()
            select new { unit, HostId = property != null ? property.HostId : default }
        ).SingleOrDefaultAsync(cancellationToken);

        return row is null
            ? null
            : new UnitSummary
            {
                Id = row.unit.Id,
                Name = new Dictionary<string, string>(row.unit.Name.Values),
                MaxOccupancy = row.unit.MaxOccupancy,
                BasePrice = row.unit.BasePrice,
                PropertyId = row.unit.PropertyId,
                HostId = row.HostId,
                CancellationPolicy = row.unit.CancellationPolicy
            };
    }

    public async Task<IReadOnlyDictionary<Guid, UnitSummary>> GetUnitsAsync(IEnumerable<Guid> unitIds, CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. unitIds];

        // Same materialize-first-map-after constraint, and same left-join
        // reasoning, as GetUnitAsync above.
        var rows = await (
            from unit in dbContext.Units.AsNoTracking()
            where ids.Contains(unit.Id)
            join property in dbContext.Properties.AsNoTracking() on unit.PropertyId equals property.Id into properties
            from property in properties.DefaultIfEmpty()
            select new { unit, HostId = property != null ? property.HostId : default }
        ).ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.unit.Id,
            row => new UnitSummary
            {
                Id = row.unit.Id,
                Name = new Dictionary<string, string>(row.unit.Name.Values),
                MaxOccupancy = row.unit.MaxOccupancy,
                BasePrice = row.unit.BasePrice,
                PropertyId = row.unit.PropertyId,
                HostId = row.HostId,
                CancellationPolicy = row.unit.CancellationPolicy
            });
    }

    public async Task<IReadOnlyList<Guid>> GetUnitIdsForHostAsync(Guid hostId, CancellationToken cancellationToken)
    {
        return await dbContext.Units.AsNoTracking()
            .Where(u => dbContext.Properties.Where(p => p.HostId == hostId).Select(p => p.Id).Contains(u.PropertyId))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<StayPricingResult?> ResolveStayPricingAsync(
        Guid unitId, DateOnly checkIn, DateOnly checkOut, CancellationToken cancellationToken)
    {
        Unit? unit = await dbContext.Units.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == unitId, cancellationToken);

        if (unit is null)
        {
            return null;
        }

        List<PricingRule> rules = await dbContext.PricingRules.AsNoTracking()
            .Where(r => r.UnitId == unitId)
            .ToListAsync(cancellationToken);

        StayPriceBreakdown breakdown = PricingCalculator.ResolveStayTotal(unit.BasePrice, checkIn, checkOut, rules);

        return new StayPricingResult
        {
            MaxOccupancy = unit.MaxOccupancy,
            TotalPrice = breakdown.Total,
            Subtotal = breakdown.Subtotal.Amount,
            LengthOfStayDiscountAmount = breakdown.LengthOfStayDiscountAmount
        };
    }
}
