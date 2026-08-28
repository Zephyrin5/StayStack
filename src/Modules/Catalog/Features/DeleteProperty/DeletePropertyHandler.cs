using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Catalog.Entities;
using Catalog.Features.DeleteUnit;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
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

        DateTimeOffset now = timeProvider.GetUtcNow();

        // Archiving the Property alone would leave its Units still Active -
        // reachable via any query that goes through Units directly rather
        // than through Property first (GetPropertyById does, but a future
        // caller might not). Archiving them together keeps "this property
        // is gone" true everywhere, not just where a caller happens to
        // join through Property first.
        List<Unit> units = await dbContext.Units
            .Where(u => u.PropertyId == property.Id)
            .ToListAsync(cancellationToken);

        // Same guard as DeleteUnitHandler, per unit - a cascading archive
        // shouldn't be a back door around the single-unit check.
        foreach (Unit unit in units)
        {
            await DeleteUnitHandler.EnsureNoActiveBookingsOrHoldsAsync(unit.Id, timeProvider, unitArchivalGuard, availabilityLookup, cancellationToken);
        }

        foreach (Unit unit in units)
        {
            unit.Archive(now, currentUserProvider.UserId);
        }

        property.Archive(now, currentUserProvider.UserId);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new DeletePropertyResponse { PropertyId = property.Id };
    }
}
