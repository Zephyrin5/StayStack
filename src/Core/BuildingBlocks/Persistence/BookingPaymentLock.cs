using System.Data.Common;
namespace BuildingBlocks.Persistence;

/// <summary>
///     Mutual exclusion between cancelling a booking and opening a payment against it. Cancellation's
///     <c>FOR UPDATE</c> on the booking row would exclude a payment perfectly well if the payment side
///     could take it, and it cannot: naming <c>bookings</c> from Transactions is the coupling
///     docs/adr/0004 exists to prevent.
///     <para>
///         Exclusive on both sides. Held by every path that cancels a booking and by initiation; a
///         cancelling path holding only the booking row is invisible to initiation, which never
///         touches that row. Acquiring is the only way to get a <see cref="BookingPaymentLockHandle"/>,
///         and <c>Booking.Cancel</c> and the row claim both require one, so such a path no longer
///         compiles.
///     </para>
///     <para>
///         <b>This lock first, then the booking row lock</b> (docs/adr/0028). A path waiting on the
///         advisory lock then holds no row lock while it waits, so payment confirmation is never
///         queued behind an initiation it has nothing to do with.
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
