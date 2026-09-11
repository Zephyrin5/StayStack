using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Archival;
using Catalog.Contracts;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
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
        Unit? unit = await dbContext.Units
            .SingleOrDefaultAsync(u => u.Id == request.UnitId, cancellationToken);

        if (unit is null)
        {
            throw new NotFoundException(nameof(Unit), request.UnitId);
        }

        Property? property = await dbContext.Properties
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
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            await UnitArchival.EnsureArchivableAsync(
                dbContext, unit.Id, property.TimeZoneId, timeProvider,
                unitArchivalGuard, availabilityLookup, cancellationToken);

            unit.Archive(timeProvider.GetUtcNow(), currentUserProvider.UserId);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return new DeleteUnitResponse { UnitId = unit.Id };
    }
}
