using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Catalog.Entities;
using Catalog.Exceptions;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Unit = Catalog.Entities.Unit;
namespace Catalog.Features.DeleteUnit;

public class DeleteUnitHandler(
    AppCatalogDbContext dbContext,
    ICurrentUserProvider currentUserProvider,
    IHostAuthorization hostAuthorization,
    IUnitArchivalGuard unitArchivalGuard,
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

        await EnsureNoActiveBookingsOrHoldsAsync(unit.Id, timeProvider, dbContext, unitArchivalGuard, cancellationToken);

        unit.Archive(timeProvider.GetUtcNow(), currentUserProvider.UserId);

        await dbContext.SaveChangesAsync(cancellationToken);

        return new DeleteUnitResponse { UnitId = unit.Id };
    }

    // Shared with DeletePropertyHandler's per-unit cascade. A held/booked
    // hold or a live booking both mean someone is actively transacting
    // against this unit - archiving out from under either one is exactly
    // the mid-checkout 404 (ConfirmBookingHandler's unitLookup.GetUnitAsync
    // returning null) this guard exists to prevent, on top of the more
    // obvious case of pulling a unit out from under a guest mid-stay.
    internal static async Task EnsureNoActiveBookingsOrHoldsAsync(
        Guid unitId,
        TimeProvider timeProvider,
        AppCatalogDbContext dbContext,
        IUnitArchivalGuard unitArchivalGuard,
        CancellationToken cancellationToken)
    {
        DateOnly today = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime);

        bool hasActiveBooking = await unitArchivalGuard.HasActiveBookingForUnitAsync(unitId, today, cancellationToken);
        if (hasActiveBooking)
        {
            throw new UnitHasActiveBookingsException(unitId);
        }

        bool hasActiveHold = await dbContext.UnitAvailabilityHolds.AsNoTracking()
            .AnyAsync(h => h.UnitId == unitId && (h.Status == "held" || h.Status == "booked"), cancellationToken);
        if (hasActiveHold)
        {
            throw new UnitHasActiveBookingsException(unitId);
        }
    }
}
