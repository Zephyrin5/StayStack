using Hosts.Entities;
using Microsoft.EntityFrameworkCore;
namespace Hosts.Contracts;

// internal, same reasoning as HostLookup/HostAuthorization - Catalog/Identity
// should only ever reach this through IHostRegistrar, resolved via DI.
internal class HostRegistrar(AppHostsDbContext dbContext) : IHostRegistrar
{
    public async Task<Guid> RegisterHostAsync(
        string businessName,
        string contactEmail,
        string? contactPhone,
        CancellationToken cancellationToken)
    {
        Host host = Host.Create(businessName, contactEmail, contactPhone);

        dbContext.Hosts.Add(host);
        await dbContext.SaveChangesAsync(cancellationToken);

        return host.Id;
    }

    public async Task DeleteAsync(Guid hostId, CancellationToken cancellationToken)
    {
        Host? host = await dbContext.Hosts.SingleOrDefaultAsync(h => h.Id == hostId, cancellationToken);
        if (host is null)
        {
            return;
        }

        dbContext.Hosts.Remove(host);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
