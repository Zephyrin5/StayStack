using Microsoft.EntityFrameworkCore;
namespace Catalog.Contracts;

// internal, same reasoning as UnitLookup - Bookings should only ever reach
// this through IHoldLookup, resolved via DI.
internal class HoldLookup(AppCatalogDbContext dbContext) : IHoldLookup
{
    public async Task<IReadOnlyList<Guid>> GetBookedHoldIdsOlderThanAsync(
        DateTimeOffset cutoff, DateTimeOffset earliestBookedAt, int maxResults, CancellationToken cancellationToken)
    {
        return await dbContext.UnitAvailabilityHolds.AsNoTracking()
            .Where(h => h.Status == "booked" && h.BookedAt != null && h.BookedAt <= cutoff && h.BookedAt > earliestBookedAt)
            .OrderBy(h => h.BookedAt)
            .Take(maxResults)
            .Select(h => h.Id)
            .ToListAsync(cancellationToken);
    }
}
