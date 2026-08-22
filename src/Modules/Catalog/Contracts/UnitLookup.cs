using Microsoft.EntityFrameworkCore;
namespace Catalog.Contracts;

// internal, same reasoning as Hosts.Contracts' implementations - Bookings
// should only ever reach this through IUnitLookup, resolved via DI.
internal class UnitLookup(AppCatalogDbContext dbContext) : IUnitLookup
{
    public Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken)
    {
        // Plain scalar fields only, no LocalizedText involved - safe to
        // project server-side, unlike GetPropertyByIdHandler's Name field.
        return dbContext.Units.AsNoTracking()
            .Where(u => u.Id == unitId)
            .Select(u => new UnitSummary
            {
                Id = u.Id,
                MaxOccupancy = u.MaxOccupancy,
                BasePrice = u.BasePrice,
                Currency = u.Currency
            })
            .SingleOrDefaultAsync(cancellationToken);
    }
}
