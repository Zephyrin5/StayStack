using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Contracts;

// internal, same reasoning as Hosts.Contracts' implementations - Bookings
// should only ever reach this through IUnitLookup, resolved via DI.
internal class UnitLookup(AppCatalogDbContext dbContext) : IUnitLookup
{
    public async Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken)
    {
        // Materialize first, map after - Name is a LocalizedText (a
        // value-converted jsonb column via StayStackDbContext's global
        // convention), and EF Core can't translate .Values access on a
        // converted CLR type into SQL inside a server-side .Select(). Same
        // constraint GetPropertyByIdHandler (Catalog) already documents.
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
}
