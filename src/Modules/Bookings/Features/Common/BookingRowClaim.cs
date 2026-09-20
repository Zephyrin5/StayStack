using BuildingBlocks.Persistence;
using Dapper;
using System.Data.Common;
namespace Bookings.Features.Common;

/// <summary>
///     The booking row lock, taken after <see cref="BookingPaymentLock"/> and never before
///     (docs/adr/0028). The handle is the parameter that enforces the order: there is no way to claim
///     the row without one, and the only way to get one is to take the advisory lock.
/// </summary>
internal static class BookingRowClaim
{
    /// <summary>
    ///     Waits for the row. The caller wants the committed outcome - a cancellation answering a
    ///     guest, or a payment learning whether a refund is owed.
    /// </summary>
    public static async Task<bool> ClaimAsync(
        DbConnection connection, DbTransaction transaction, BookingPaymentLockHandle heldLock, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            $"""SELECT id FROM {BookingsModel.Schema}.bookings WHERE id = @BookingId FOR UPDATE""",
            new { BookingId = heldLock.BookingId },
            transaction,
            cancellationToken: cancellationToken)) is not null;

    /// <summary>
    ///     Steps over a row somebody else holds, for a sweep that would otherwise put every booking
    ///     behind one contended row. False means "not this run", never "gone".
    /// </summary>
    public static async Task<bool> TryClaimAsync(
        DbConnection connection, DbTransaction transaction, BookingPaymentLockHandle heldLock, CancellationToken cancellationToken) =>
        await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            $"""SELECT id FROM {BookingsModel.Schema}.bookings WHERE id = @BookingId FOR UPDATE SKIP LOCKED""",
            new { BookingId = heldLock.BookingId },
            transaction,
            cancellationToken: cancellationToken)) is not null;
}
