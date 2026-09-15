using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Archival;
using Catalog.Contracts;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using SeedWork.Enums;
using Unit = Catalog.Entities.Unit;
namespace Catalog.Features.DeleteUnit;

public class DeleteUnitHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
    IUnitArchivalGuard unitArchivalGuard,
    IUnitAvailabilityLookup availabilityLookup,
    TimeProvider timeProvider) : IRequestHandler<DeleteUnitRequest, DeleteUnitResponse>
{
    public async ValueTask<DeleteUnitResponse> Handle(DeleteUnitRequest request, CancellationToken cancellationToken)
    {
        // Loaded for the ownership check only. Nothing inside the retried
        // delegate below may touch these instances - see the comment there.
        Unit? unit = await dbContext.Units.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == request.UnitId, cancellationToken);

        if (unit is null)
        {
            throw new NotFoundException(nameof(Unit), request.UnitId);
        }

        Property? property = await dbContext.Properties.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Id == unit.PropertyId, cancellationToken);

        if (property is null)
        {
            throw new NotFoundException(nameof(Property), unit.PropertyId);
        }

        if (!currentUserProvider.Roles.Contains(AuthorizationPolicies.Administrator))
        {
            hostAuthorization.RequireOwnership(property.HostId, nameof(Property), property.Id);
        }

        // The guard and the archive have to be one atomic decision, and they
        // were two. Nothing spanned them, so a hold could be inserted between
        // "no active holds" and the archive landing - leaving a unit archived
        // with live inventory against it. A second unlocked check just before
        // SaveChanges would narrow that window without closing it.
        //
        // The lock is inside UnitArchival now rather than here, because
        // DeletePropertyHandler shares the check and did not share the lock.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            // Cleared and reloaded inside the delegate (docs/adr/0025).
            // SaveChangesAsync accepts changes when it returns
            // (acceptAllChangesOnSuccess defaults to true), so an instance loaded
            // before the retry has Archived as its original value before
            // CommitAsync runs. A transient commit failure then retries a delegate
            // where Archive() assigns a value EF no longer considers a change: the
            // UPDATE carries the audit columns and not the status, the commit
            // succeeds, and this handler reports success over an Active unit.
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // IgnoreQueryFilters, because the soft-delete filter hides exactly
            // the row a retry needs to see. An attempt that committed and lost
            // its acknowledgement leaves the unit archived; without this the
            // reload finds nothing and reports 404 for an archival that
            // succeeded.
            //
            // Scoped deliberately. IgnoreQueryFilters is query-wide rather than
            // entity-wide, so it belongs only on a query whose single root is the
            // row being archived - never on one that also reaches Units or
            // Properties for some other purpose.
            Unit locked = await dbContext.Units.IgnoreQueryFilters()
                              .SingleOrDefaultAsync(u => u.Id == request.UnitId, cancellationToken)
                          ?? throw new NotFoundException(nameof(Unit), request.UnitId);

            // Already archived: an earlier attempt of this same delegate
            // committed. Recovered success, not a conflict - the caller asked
            // for this unit to be archived and it is.
            //
            // Verify-before-compensate, in its smallest form: ask the database
            // what happened rather than inferring from how the attempt ended.
            if (locked.Status == EntityStatus.Archived)
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }

            // Reloaded too, rather than read off the detached instance: the
            // time zone decides what "today" means to the archival guard, and
            // reading it from an instance this delegate has just detached is
            // the same mistake one level down.
            Property lockedProperty = await dbContext.Properties
                                          .SingleOrDefaultAsync(p => p.Id == locked.PropertyId, cancellationToken)
                                      ?? throw new NotFoundException(nameof(Property), locked.PropertyId);

            await UnitArchival.EnsureArchivableAsync(
                dbContext, locked.Id, lockedProperty.TimeZoneId, timeProvider,
                unitArchivalGuard, availabilityLookup, cancellationToken);

            // Safe to repeat after an ambiguous commit: Entity.Archive is a
            // plain assignment with no already-archived guard, so a retry that
            // finds the unit archived writes the same value rather than
            // throwing. The reload above is what makes that a real no-op
            // instead of a silently skipped one.
            locked.Archive(timeProvider.GetUtcNow(), currentUserProvider.UserId);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return new DeleteUnitResponse { UnitId = request.UnitId };
    }
}
