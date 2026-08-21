using Microsoft.EntityFrameworkCore;
namespace Hosts.Contracts;

// internal, same reasoning as HostRegistrar - Catalog/Identity should only
// ever reach this through IHostLookup, resolved via DI.
internal class HostLookup(AppHostsDbContext dbContext) : IHostLookup
{
    public Task<bool> ExistsAsync(Guid hostId, CancellationToken cancellationToken)
    {
        return dbContext.Hosts.AnyAsync(h => h.Id == hostId, cancellationToken);
    }
}
