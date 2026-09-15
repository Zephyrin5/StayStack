using BuildingBlocks.Persistence;
using BuildingBlocks.Time;
using Catalog.Contracts;
using Catalog.Exceptions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace Catalog.Archival;

/// <summary>
///     The one way to decide a unit may be archived.
///     <para>
///         The check is correct only under the unit's lock inside a
///         transaction, so the lock and the check are one call, shared by
///         DeleteUnitHandler and DeletePropertyHandler, and the transaction is
///         asserted rather than assumed (docs/adr/0028).
///     </para>
/// </summary>
public static class UnitArchival
{
    /// <summary>
    ///     Takes this unit's availability lock exclusively and then verifies
    ///     nothing live is transacting against it. Both, in that order, or the
    ///     check means nothing.
    ///     <para>
    ///         The caller must already be in a transaction that also carries
    ///         the archiving write. Advisory locks here are transaction-scoped,
    ///         so a caller without one releases the lock the instant this
    ///         returns - re-opening the exact gap this closes, and doing it
    ///         invisibly. Hence the throw rather than a comment.
    ///     </para>
    ///     <para>
    ///         A caller archiving several units must call this for all of them
    ///         before committing, so every lock is still held when the archive
    ///         lands. Acquiring and checking one unit at a time and then
    ///         archiving is not enough: a hold can be taken on unit three after
    ///         unit three passed its own check.
    ///     </para>
    /// </summary>
    public static async Task EnsureArchivableAsync(
        AppCatalogDbContext dbContext,
        Guid unitId,
        string timeZoneId,
        TimeProvider timeProvider,
        IUnitArchivalGuard unitArchivalGuard,
        IUnitAvailabilityLookup availabilityLookup,
        CancellationToken cancellationToken)
    {
        IDbContextTransaction transaction = dbContext.Database.CurrentTransaction
                                            ?? throw new InvalidOperationException(
                                                $"{nameof(EnsureArchivableAsync)} must run inside a transaction. Its advisory lock is " +
                                                "transaction-scoped, so without one it is released before the archive it guards is written.");

        // SQLite in the unit tests has no advisory locks. The check below still
        // runs there - it just cannot provide cross-connection exclusion, the
        // same split every other Postgres-specific claim in this codebase makes.
        if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            await dbContext.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
                UnitAvailabilityLock.AcquireForArchivalSql,
                new { LockKey = UnitAvailabilityLock.KeyFor(unitId) },
                transaction.GetDbTransaction(),
                cancellationToken: cancellationToken));
        }

        // A live hold or a live booking both mean someone is actively
        // transacting against this unit - archiving out from under either one
        // is exactly the mid-checkout 404 (ConfirmBookingHandler's
        // unitLookup.GetUnitAsync returning null) this guard exists to prevent,
        // on top of the more obvious case of pulling a unit out from under a
        // guest mid-stay.
        //
        // ---------------------------------------------------------------
        // HOLDS ARE CHECKED FIRST. THE ORDER IS LOAD-BEARING. DO NOT SWAP.
        // ---------------------------------------------------------------
        //
        // The lock above excludes *new* holds. It does not freeze an existing
        // one: neither ConfirmHoldAsync nor MarkHoldPaidAsync acquires it, so a
        // hold that already existed when this method started can advance
        // held -> pending_payment -> booked while these two checks run.
        //
        // HasActiveHoldForUnitAsync deliberately excludes 'booked' (a sold hold
        // is never deleted, so counting it made a unit unarchivable for life -
        // see its own contract). That exclusion is what makes the ordering
        // matter, because it means the hold check has a blind spot exactly
        // where the booking check has coverage.
        //
        // With bookings checked first, a checkout completing in the gap fell
        // through both: no booking existed yet when the booking check ran, and
        // by the time the hold check ran the hold had reached 'booked' and was
        // excluded. The unit was archived with a live paid stay against it.
        //
        // Checking holds first means the check that runs *last* covers the
        // states the first one can transition into, and 'booked' implies a
        // Confirmed booking - MarkHoldPaidAsync sets it in the same transaction
        // as Booking.Confirm(), so the row the booking check needs is always
        // committed by then. Every interleaving:
        //
        //   'held' / 'pending_payment' at the hold check -> hold check throws
        //   already 'booked'                             -> booking check throws
        //   reaches 'booked' between the two checks       -> booking check throws
        //   no hold at all                                -> the lock stops one appearing
        //
        // The historical case still archives, which is the behaviour restored
        // deliberately a few rounds ago: a 'booked' hold whose stay has ended is
        // invisible to the hold check, and HasActiveBookingForUnitAsync filters
        // CheckOut >= today, so both pass.
        //
        // The UTC instant, not the property-local `today` below. A hold's
        // expiry is a timestamp rather than a date - the two questions are
        // measured in different units and only one of them is a calendar day.
        if (await availabilityLookup.HasActiveHoldForUnitAsync(unitId, timeProvider.GetUtcNow(), cancellationToken))
        {
            throw new UnitHasActiveBookingsException(unitId);
        }

        // The property's own zone, not UTC - "is a booking still active" is
        // measured against CheckOut, itself a property-local date. Both callers
        // already have the Property loaded. See docs/adr/0018.
        DateOnly today = PropertyTimeZone.Today(timeProvider, timeZoneId);

        if (await unitArchivalGuard.HasActiveBookingForUnitAsync(unitId, today, cancellationToken))
        {
            throw new UnitHasActiveBookingsException(unitId);
        }
    }
}
