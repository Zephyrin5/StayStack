using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Entities;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Unit = Catalog.Entities.Unit;
namespace Catalog.Features.DeleteUnit;

public class DeleteUnitHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
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

        if (!currentUserProvider.Roles.Contains("Administrator"))
        {
            hostAuthorization.RequireOwnership(property.HostId, nameof(Property), property.Id);
        }

        // Deliberately does NOT touch unit_availability_holds - a held or
        // booked range against this unit stays exactly as it is (the
        // exclusion constraint's job, not this handler's); archiving only
        // stops the unit from being listed/searched/added to going
        // forward. See ADR-0010 for why that table is never written to
        // through EF change tracking at all.
        unit.Archive(timeProvider.GetUtcNow(), currentUserProvider.UserId);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new DeleteUnitResponse { UnitId = unit.Id };
    }
}
