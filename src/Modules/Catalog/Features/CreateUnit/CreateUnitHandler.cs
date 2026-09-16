using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Localization;
using BuildingBlocks.Persistence;
using Catalog.Archival;
using Catalog.Entities;
using Dapper;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using SeedWork.ValueObjects;
using Unit = Catalog.Entities.Unit;

namespace Catalog.Features.CreateUnit;

public class CreateUnitHandler(
    CatalogDb dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
    IOptions<LocalizationSettings> localizationSettings) : IRequestHandler<CreateUnitRequest, CreateUnitResponse>
{
    public async ValueTask<CreateUnitResponse> Handle(CreateUnitRequest request, CancellationToken cancellationToken)
    {
        // Minted before anything that could retry (docs/adr/0025).
        Guid unitId = Guid.CreateVersion7();

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

        LocalizedText name = LocalizedText.Create(request.Name, localizationSettings.Value.DefaultCulture);

        // Omitted tiers use Unit.Create's default policy.
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

        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // Past the soft-delete filter: the unit is this request's earlier committed attempt (docs/adr/0025).
            if (await dbContext.Units.IgnoreQueryFilters().AnyAsync(u => u.Id == unit.Id, cancellationToken))
            {
                await transaction.RollbackAsync(cancellationToken);
                return;
            }

            // Shared against DeletePropertyHandler's exclusive lock, so no unit lands under an archived property.
            await dbContext.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
                AdvisoryLock.AcquireSharedSql,
                new { LockKey = PropertyUnitsLock.KeyFor(request.PropertyId) },
                transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));

            // Re-read under the lock; an archived property is not found.
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
