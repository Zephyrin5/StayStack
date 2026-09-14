using Persistence;
using Hosts.Entities;
using Microsoft.EntityFrameworkCore;
namespace Hosts.Contracts;

// internal, same reasoning as HostLookup/HostAuthorization - Catalog/Identity
// should only ever reach this through IHostRegistrar, resolved via DI.
internal class HostRegistrar(AppHostsDbContext dbContext) : IHostRegistrar
{
    public async Task RegisterHostAsync(
        Guid hostId,
        string businessName,
        string contactEmail,
        string? contactPhone,
        CancellationToken cancellationToken)
    {
        // Checked first so an ordinary retry is a cheap no-op rather than a
        // caught exception. IgnoreQueryFilters because an archived Host still
        // occupies this id - re-inserting over it would violate the primary
        // key, and "already registered" is the right answer either way.
        bool alreadyRegistered = await dbContext.Hosts
            .IgnoreQueryFilters()
            .AnyAsync(h => h.Id == hostId, cancellationToken);

        if (alreadyRegistered)
        {
            return;
        }

        Host host = Host.Create(hostId, businessName, contactEmail, contactPhone);
        dbContext.Hosts.Add(host);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsPrimaryKeyViolationOf<Host>(dbContext))
        {
            // Two callers raced the check above with the same id, or this save's
            // own retry met its committed row. The row exists, which is all this
            // method promises - detach the copy and report success.
            dbContext.ChangeTracker.Clear();
        }
    }

    public async Task DeleteAsync(Guid hostId, CancellationToken cancellationToken)
    {
        // IgnoreQueryFilters, matching RegisterHostAsync above. The two reads
        // disagreed, and the asymmetry pointed the wrong way: registration saw
        // archived rows and would adopt one, while this could not see it to
        // clean up - so a compensation would silently no-op and leave the user
        // linked to a Host nothing could remove.
        //
        // Fixed by widening this rather than narrowing that, because narrowing
        // does not actually avoid the problem. An archived Host still occupies
        // the primary key, so a filtered existence check misses it, the insert
        // hits a unique violation, and the catch there treats that as "already
        // registered" - adopting the archived row anyway, just implicitly and
        // by way of an exception. Registration has to see archived rows; this
        // therefore has to be able to reach whatever registration adopted.
        //
        // Nothing archives a Host today, so this is latent either way - which
        // is the reason to settle it now rather than after something does.
        Host? host = await dbContext.Hosts
            .IgnoreQueryFilters()
            .SingleOrDefaultAsync(h => h.Id == hostId, cancellationToken);
        if (host is null)
        {
            return;
        }

        dbContext.Hosts.Remove(host);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
