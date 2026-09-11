using BuildingBlocks.Exceptions;
using BuildingBlocks.Time;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Catalog.Entities;
using Catalog.Exceptions;
using Hosts.Contracts;
using Mediator;
using BuildingBlocks.Persistence;
using Dapper;
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
        // Exclusive, against the shared lock HoldAvailabilityHandler takes:
        // this waits for holds already in flight and excludes new ones until
        // the archive commits.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                await dbContext.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
                    UnitAvailabilityLock.AcquireExclusiveSql,
                    new { LockKey = UnitAvailabilityLock.KeyFor(unit.Id) },
                    transaction.GetDbTransaction(),
                    cancellationToken: cancellationToken));
            }

            // Re-checked under the lock, which is the only version of this
            // check that means anything.
            await EnsureNoActiveBookingsOrHoldsAsync(
                unit.Id, property.TimeZoneId, timeProvider, unitArchivalGuard, availabilityLookup, cancellationToken);

            unit.Archive(timeProvider.GetUtcNow(), currentUserProvider.UserId);

            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        });

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
        string timeZoneId,
        TimeProvider timeProvider,
        IUnitArchivalGuard unitArchivalGuard,
        IUnitAvailabilityLookup availabilityLookup,
        CancellationToken cancellationToken)
    {
        // The property's own zone, not UTC - "is a booking still active" is
        // measured against CheckOut, itself a property-local date. Both
        // callers already have the Property loaded. See docs/adr/0018.
        DateOnly today = PropertyTimeZone.Today(timeProvider, timeZoneId);

        bool hasActiveBooking = await unitArchivalGuard.HasActiveBookingForUnitAsync(unitId, today, cancellationToken);
        if (hasActiveBooking)
        {
            throw new UnitHasActiveBookingsException(unitId);
        }

        // The UTC instant, not the property-local `today` above. A hold's
        // expiry is a timestamp rather than a date - the two questions are
        // measured in different units and only one of them is a calendar day.
        bool hasActiveHold = await availabilityLookup.HasActiveHoldForUnitAsync(
            unitId, timeProvider.GetUtcNow(), cancellationToken);
        if (hasActiveHold)
        {
            throw new UnitHasActiveBookingsException(unitId);
        }
    }
}
