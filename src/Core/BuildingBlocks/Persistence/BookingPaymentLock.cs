using System.Data.Common;
namespace BuildingBlocks.Persistence;

/// <summary>
///     Mutual exclusion between cancelling a booking and opening a payment
///     against it.
///     <para>
///         Cancellation locks the booking row with <c>FOR UPDATE</c>, which is
///         database-wide and would exclude a payment perfectly well - if the
///         payment side could take it. It cannot: naming <c>bookings</c> from
///         Transactions is the coupling docs/adr/0004 exists to prevent, and
///         the same reasoning that produced
///         <see cref="UnitAvailabilityLock"/> applies unchanged. An advisory
///         lock is keyed on a number rather than on anyone's row, so both sides
///         agree on a name without either reaching into the other's schema.
///     </para>
///     <para>
///         Taken <b>exclusively by both</b> sides, unlike the shared/exclusive
///         split for holds. Two concurrent initiations for one booking are
///         already refused by the active-transaction index, so there is no
///         parallelism here worth preserving, and a shared mode would only
///         create a second way to get the pairing wrong.
///     </para>
///     <para>
///         Held by <b>every path that cancels a booking</b> - today
///         CancelBookingHandler and ExpireUnpaidBookingsJob - and by
///         initiation. A cancelling path that holds only the booking row is
///         invisible to initiation, which never touches that row; expiry was
///         exactly that for a while. Acquiring is the only way to get a
///         <see cref="BookingPaymentLockHandle"/>, and <c>Booking.Cancel</c> and the row claim both
///         require one, so such a path no longer compiles.
///     </para>
///     <para>
///         <b>Ordering: this lock first, then the booking row lock.</b> Two
///         paths take both, so the order is load-bearing rather than
///         incidental. Taking the advisory lock first also means a path
///         waiting on it holds no row lock while it waits, so payment
///         confirmation - which takes the row lock alone - is never queued
///         behind an initiation it has nothing to do with.
///     </para>
/// </summary>
public static class BookingPaymentLock
{
    public static long KeyFor(Guid bookingId) => AdvisoryLock.KeyFor(Scope, bookingId);

    /// <summary>
    ///     Waits for the lock. The caller's own transaction holds it, so it is released when that
    ///     transaction ends however it ends.
    /// </summary>
    public static async Task<BookingPaymentLockHandle> AcquireAsync(
        DbConnection connection, DbTransaction transaction, Guid bookingId, CancellationToken cancellationToken)
    {
        await using DbCommand command = Command(connection, transaction, AdvisoryLock.AcquireExclusiveSql, bookingId);
        await command.ExecuteNonQueryAsync(cancellationToken);

        return new BookingPaymentLockHandle(bookingId);
    }

    /// <summary>
    ///     Takes the lock if it is free, and answers null if somebody else holds it - the sweep's
    ///     side, which steps over a contended booking and finds it again next run.
    /// </summary>
    public static async Task<BookingPaymentLockHandle?> TryAcquireAsync(
        DbConnection connection, DbTransaction transaction, Guid bookingId, CancellationToken cancellationToken)
    {
        await using DbCommand command = Command(connection, transaction, AdvisoryLock.TryAcquireExclusiveSql, bookingId);

        return await command.ExecuteScalarAsync(cancellationToken) is true
            ? new BookingPaymentLockHandle(bookingId)
            : null;
    }

    // Plain ADO rather than Dapper: BuildingBlocks carries no data-access package, and one parameter
    // is not worth one.
    private static DbCommand Command(DbConnection connection, DbTransaction transaction, string sql, Guid bookingId)
    {
        DbCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;

        DbParameter key = command.CreateParameter();
        key.ParameterName = "LockKey";
        key.Value = KeyFor(bookingId);
        command.Parameters.Add(key);

        return command;
    }

    // Part of the key rather than a label, so it is a wire format between
    // deployments - see AdvisoryLock.KeyFor.
    private const string Scope = "booking-payment";
}
