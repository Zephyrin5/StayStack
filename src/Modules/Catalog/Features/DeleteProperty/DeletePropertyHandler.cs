using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using BuildingBlocks.Persistence;
using Catalog.Archival;
using Catalog.Entities;
using Dapper;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using SeedWork.Enums;
using Microsoft.EntityFrameworkCore.Storage;
using Unit = Catalog.Entities.Unit;
namespace Catalog.Features.DeleteProperty;

public class DeletePropertyHandler(
    AppCatalogDbContext dbContext,
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

        // One transaction spanning every guard, every lock and the archive
        // itself. This handler had none, and with an advisory lock that is not
        // a weaker version of the single-unit path - it is a broken one: those
        // locks release at transaction end, so without an explicit transaction
        // each unit's lock would be gone before the next unit was even checked.
        //
        // Two races were live, and they need different locks:
        //
        //  - A hold taken on unit three after unit three passed its guard.
        //    Every unit lock is therefore held until the commit, rather than
        //    taken and released one unit at a time.
        //  - A unit created after the read below, which is then neither checked
        //    nor archived - a live unit under an archived property, the orphan
        //    state UnitLookup throws OrphanedUnitException for. No per-unit
        //    lock can cover that, because the unit did not exist to be locked.
        //    Hence the property lock, which CreateUnitHandler takes shared.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            // A retry re-reads and re-locks everything from scratch. The
            // property and any units loaded before this delegate ran describe a
            // world a previous attempt may already have changed (docs/adr/0025).
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                await dbContext.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
                    AdvisoryLock.AcquireExclusiveSql,
                    new { LockKey = PropertyUnitsLock.KeyFor(request.PropertyId) },
                    transaction.GetDbTransaction(),
                    cancellationToken: cancellationToken));
            }

            // Re-read under that lock, not carried in from the authorization
            // check above: taking the lock orders this against unit creation
            // but tells it nothing about what happened before it got there.
            // IgnoreQueryFilters and the archived check below, for the same
            // reason as the single-unit path: a committed-but-unacknowledged
            // attempt leaves this property archived, and the filtered reload
            // would then report 404 for work that succeeded.
            //
            // Query-wide rather than entity-wide, so this is confined to the
            // one query whose root is the property being archived. The unit
            // query below deliberately keeps its filter: those are the units
            // still live under this property, which is exactly what it should
            // see.
            Property locked = await dbContext.Properties.IgnoreQueryFilters()
                                  .SingleOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken)
                              ?? throw new NotFoundException(nameof(Property), request.PropertyId);

            if (locked.Status == EntityStatus.Archived)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }

            // Archiving the Property alone would leave its Units still Active -
            // reachable via any query that goes through Units directly rather
            // than through Property first (GetPropertyById does, but a future
            // caller might not). Archiving them together keeps "this property
            // is gone" true everywhere, not just where a caller happens to
            // join through Property first.
            //
            // Ordered by id, so two callers archiving the same property take
            // the unit locks in the same sequence. ToListAsync alone promises
            // no order, and two interleaved runs acquiring the same locks in
            // opposite orders is the textbook deadlock. The property lock above
            // already serialises those two, so this is the second line of
            // defence rather than the first - but it costs an ORDER BY on a
            // handful of rows, and the failure it prevents is a 40P01 under a
            // retry policy that would silently absorb it.
            List<Unit> units = await dbContext.Units
                .Where(u => u.PropertyId == locked.Id)
                .OrderBy(u => u.Id)
                .ToListAsync(cancellationToken);

            // Every guard runs, and every lock it takes stays held, before any
            // archive is written. Same guard as DeleteUnitHandler - a cascading
            // archive must not be a back door around the single-unit check.
            foreach (Unit unit in units)
            {
                await UnitArchival.EnsureArchivableAsync(
                    dbContext, unit.Id, locked.TimeZoneId, timeProvider,
                    unitArchivalGuard, availabilityLookup, cancellationToken);
            }

            DateTimeOffset now = timeProvider.GetUtcNow();

            foreach (Unit unit in units)
            {
                unit.Archive(now, currentUserProvider.UserId);
            }

            locked.Archive(now, currentUserProvider.UserId);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return new DeletePropertyResponse { PropertyId = property.Id };
    }
}
