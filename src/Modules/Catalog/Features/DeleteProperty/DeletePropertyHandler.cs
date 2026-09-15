using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Persistence;
using Catalog.Archival;
using Catalog.Contracts;
using Catalog.Entities;
using Dapper;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SeedWork.Enums;
using Unit = Catalog.Entities.Unit;
namespace Catalog.Features.DeleteProperty;

public class DeletePropertyHandler(
    AppCatalogDbContext dbContext,
    IAtomicScope atomicScope,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
    IUnitArchivalGuard unitArchivalGuard,
    IUnitAvailabilityLookup availabilityLookup,
    TimeProvider timeProvider) : IRequestHandler<DeletePropertyRequest, DeletePropertyResponse>
{
    public async ValueTask<DeletePropertyResponse> Handle(DeletePropertyRequest request, CancellationToken cancellationToken)
    {
        Property? property = await dbContext.Properties
            .SingleOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken);

        if (property is null)
        {
            throw new NotFoundException(nameof(Property), request.PropertyId);
        }

        if (!currentUserProvider.Roles.Contains(AuthorizationPolicies.Administrator))
        {
            hostAuthorization.RequireOwnership(property.HostId, nameof(Property), request.PropertyId);
        }

        // One transaction holds the property lock (excludes unit creation) and every unit lock
        // (excludes new holds) until the archive commits (docs/adr/0028).
        await atomicScope.ExecuteAsync(
            AtomicParticipants.Catalog,
            AtomicParticipants.Catalog | AtomicParticipants.Bookings,
            async token =>
            {
                await dbContext.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
                    AdvisoryLock.AcquireExclusiveSql,
                    new { LockKey = PropertyUnitsLock.KeyFor(request.PropertyId) },
                    dbContext.Database.CurrentTransaction!.GetDbTransaction(),
                    cancellationToken: token));

                // Past the soft-delete filter: an archived property is this request's earlier committed attempt (docs/adr/0025).
                Property locked = await dbContext.Properties.IgnoreQueryFilters()
                                      .SingleOrDefaultAsync(p => p.Id == request.PropertyId, token)
                                  ?? throw new NotFoundException(nameof(Property), request.PropertyId);

                if (locked.Status == EntityStatus.Archived)
                {
                    return;
                }

                // Ordered by id so concurrent archivals take unit locks in one order.
                List<Unit> units = await dbContext.Units
                    .Where(u => u.PropertyId == locked.Id)
                    .OrderBy(u => u.Id)
                    .ToListAsync(token);

                // Every unit is checked, and stays locked, before any archive is written.
                foreach (Unit unit in units)
                {
                    await UnitArchival.EnsureArchivableAsync(
                        dbContext, unit.Id, locked.TimeZoneId, timeProvider,
                        unitArchivalGuard, availabilityLookup, token);
                }

                DateTimeOffset now = timeProvider.GetUtcNow();

                foreach (Unit unit in units)
                {
                    unit.Archive(now, currentUserProvider.UserId);
                }

                locked.Archive(now, currentUserProvider.UserId);

                await dbContext.SaveChangesAsync(token);
            },
            cancellationToken);

        return new DeletePropertyResponse { PropertyId = property.Id };
    }
}
