using BuildingBlocks.Persistence;
using BuildingBlocks.Time;
using Catalog.Contracts;
using Catalog.Exceptions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace Catalog.Archival;

/// <summary>The lock and the check that decide a unit may be archived (docs/adr/0028).</summary>
public static class UnitArchival
{
    /// <summary>
    ///     Takes the unit's availability lock, then checks for live holds and bookings. The caller's
    ///     transaction must carry the archive, and must call this for every unit before archiving any.
    /// </summary>
    public static async Task EnsureArchivableAsync(
        CatalogDb dbContext,
        Guid unitId,
        string timeZoneId,
        TimeProvider timeProvider,
        IUnitArchivalGuard unitArchivalGuard,
        IUnitAvailabilityLookup availabilityLookup,
        CancellationToken cancellationToken)
    {
        // The lock is transaction-scoped; without a transaction it is gone before the archive lands.
        IDbContextTransaction transaction = dbContext.Database.CurrentTransaction
                                            ?? throw new InvalidOperationException(
                                                $"{nameof(EnsureArchivableAsync)} must run inside a transaction. Its advisory lock is " +
                                                "transaction-scoped, so without one it is released before the archive it guards is written.");

        await dbContext.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
            UnitAvailabilityLock.AcquireForArchivalSql,
            new { LockKey = UnitAvailabilityLock.KeyFor(unitId) },
            transaction.GetDbTransaction(),
            cancellationToken: cancellationToken));

        // Holds before bookings, and the order is load-bearing: the lock does not freeze an existing
        // hold, and the hold check excludes 'booked', which implies a confirmed booking the later check sees.
        if (await availabilityLookup.HasActiveHoldForUnitAsync(unitId, timeProvider.GetUtcNow(), cancellationToken))
        {
            throw new UnitHasActiveBookingsException(unitId);
        }

        // CheckOut is property-local (docs/adr/0018).
        DateOnly today = PropertyTimeZone.Today(timeProvider, timeZoneId);

        if (await unitArchivalGuard.HasActiveBookingForUnitAsync(unitId, today, cancellationToken))
        {
            throw new UnitHasActiveBookingsException(unitId);
        }
    }
}
