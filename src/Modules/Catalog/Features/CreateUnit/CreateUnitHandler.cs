using BuildingBlocks.Exceptions;
using BuildingBlocks.Persistence;
using Catalog.Archival;
using Dapper;
using BuildingBlocks.Identity;
using BuildingBlocks.Localization;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
using Unit = Catalog.Entities.Unit;

namespace Catalog.Features.CreateUnit;

public class CreateUnitHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
    IOptions<LocalizationSettings> localizationSettings) : IRequestHandler<CreateUnitRequest, CreateUnitResponse>
{
    public async ValueTask<CreateUnitResponse> Handle(CreateUnitRequest request, CancellationToken cancellationToken)
    {
        // Chosen first, before anything that could retry - see docs/adr/0025.
        Guid unitId = Guid.CreateVersion7();

        Property? property = await dbContext.Properties
            .SingleOrDefaultAsync(p => p.Id == request.PropertyId, cancellationToken);

        if (property is null)
        {
            throw new NotFoundException(nameof(Property), request.PropertyId);
        }

        // Administrators may create a Unit under any Property; a Host may
        // only do so under their own. Branching on role here is safe - it
        // decides whether to run an extra check, not whether to trust
        // client-supplied data over the token.
        if (!currentUserProvider.Roles.Contains(AuthorizationPolicies.Administrator))
        {
            hostAuthorization.RequireOwnership(property.HostId, nameof(Property), request.PropertyId);
        }

        LocalizedText name = LocalizedText.Create(request.Name, localizationSettings.Value.DefaultCulture);

        // null (omitted) means "use Unit.Create's own default" - only build
        // one from the request when tiers were actually provided.
        CancellationPolicy? cancellationPolicy = request.CancellationTiers is not null
            ? CancellationPolicy.Create(request.CancellationTiers)
            : null;

        Unit unit = Unit.Create(
            unitId,
            request.PropertyId,
            name,
            request.MaxOccupancy,
            request.BasePrice,
            request.Currency,
            cancellationPolicy);

        // Shared against the exclusive lock DeletePropertyHandler takes, so
        // concurrent creation under one property still runs in parallel and
        // only archival is excluded.
        //
        // Without it, a property archive that read its units a moment ago
        // commits, this insert lands, and the result is a live unit under an
        // archived property - the orphan UnitLookup throws
        // OrphanedUnitException for. No per-unit lock can cover that: there was
        // no row to lock.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // An earlier attempt may already have committed and lost its
            // acknowledgement. The unit and its id were built before this
            // delegate, so without this a retry re-added the same entity and
            // collided with its own committed row on the primary key - a 500 for
            // a unit that exists. Asked before the property re-read below, and
            // past the soft-delete filter: the commit is the outcome, and an
            // archive landing in between must not hide it (docs/adr/0025).
            if (await dbContext.Units.IgnoreQueryFilters().AnyAsync(u => u.Id == unit.Id, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }

            if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                await dbContext.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
                    AdvisoryLock.AcquireSharedSql,
                    new { LockKey = PropertyUnitsLock.KeyFor(request.PropertyId) },
                    transaction.GetDbTransaction(),
                    cancellationToken: cancellationToken));
            }

            // Re-read under the lock, and this is what makes the lock worth
            // anything. The property was resolved before this transaction
            // opened, so it was seen while it still existed; ordering the two
            // operations does not tell either what the other did. The
            // soft-delete query filter is what answers here - an archived
            // property is simply not found.
            if (!await dbContext.Properties.AnyAsync(p => p.Id == request.PropertyId, cancellationToken))
            {
                throw new NotFoundException(nameof(Property), request.PropertyId);
            }

            dbContext.Units.Add(unit);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

        return new CreateUnitResponse { UnitId = unit.Id };
    }
}
