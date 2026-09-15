using Bookings.Contracts;
using Bookings.Entities;
using BuildingBlocks.Exceptions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
namespace Bookings.Contracts;

// internal, same reasoning as Catalog.Contracts.HoldConfirmation -
// Transactions should only ever reach this through
// IBookingPaymentConfirmation, resolved via DI.
internal class BookingPaymentConfirmation(
    AppBookingsDbContext dbContext,
    IHoldConfirmation holdConfirmation) : IBookingPaymentConfirmation
{
    public async Task<bool> ConfirmPaymentAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        Guid holdId = await dbContext.Bookings.AsNoTracking()
                          .Where(b => b.Id == bookingId)
                          .Select(b => (Guid?)b.HoldId)
                          .SingleOrDefaultAsync(cancellationToken)
                      ?? throw new NotFoundException(nameof(Booking), bookingId);

        // The hold transition and the confirmation commit with the caller's
        // payment (MarkTransactionSucceededHandler's atomic scope), so a payment
        // either sells the range and confirms the stay or does neither.
        return await ConfirmUnderRowLockAsync(bookingId, holdId, cancellationToken);
    }

    /// <summary>
    ///     Transitions Pending -> Confirmed while holding the booking's row
    ///     lock, so this path arbitrates for the row the same way
    ///     ExpireUnpaidBookingsJob does.
    ///     <para>
    ///         Booking carries no concurrency token, so an EF update keyed on Id
    ///         cannot fail. Without the lock, payment reads Pending, expiry
    ///         releases the hold and commits Cancelled, and payment's UPDATE then
    ///         overwrites Cancelled with Confirmed - a Confirmed booking whose
    ///         inventory was just handed back.
    ///     </para>
    ///     <para>
    ///         The hold's 'pending_payment' -> 'booked' transition runs inside this
    ///         transaction and lock, after the cancelled-booking check, so a
    ///         payment against a booking already cancelled never marks its hold
    ///         sold.
    ///     </para>
    ///     <para>
    ///         FOR UPDATE, not SKIP LOCKED as the expiry job uses. A sweep steps
    ///         over a row someone else is working on and revisits it next run;
    ///         this has a payment in hand and must wait for the committed outcome,
    ///         which is how it learns the booking was cancelled and a refund is
    ///         owed.
    ///     </para>
    /// </summary>
    private async Task<bool> ConfirmUnderRowLockAsync(Guid bookingId, Guid holdId, CancellationToken cancellationToken)
    {
        DbTransaction transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction()
                                    ?? throw new InvalidOperationException(
                                        $"{nameof(BookingPaymentConfirmation)}.{nameof(ConfirmPaymentAsync)} must run inside the caller's " +
                                        "atomic scope: a confirmation committed on its own survives the payment it records rolling back.");

        // The id alone, through Dapper rather than FromSqlRaw: Booking
        // carries Money as a complex property and EF composes its own
        // projection over a raw query, asking for a "TotalPrice_Amount"
        // column the snake_case convention never produced. The row lock
        // belongs to the transaction either way, so the entity can be
        // read back through EF normally afterwards.
        if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
        {
            DbConnection connection = dbContext.Database.GetDbConnection();

            await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                """SELECT id FROM "bookings" WHERE id = @BookingId FOR UPDATE""",
                new { BookingId = bookingId },
                transaction,
                cancellationToken: cancellationToken));
        }

        // Re-read under the lock. Whatever this sees is committed and
        // cannot change until this transaction ends.
        Booking booking = await dbContext.Bookings
                              .SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), bookingId);

        // Checked explicitly rather than letting Booking.Confirm()'s own
        // guard throw - a booking cancelled while this payment was in
        // flight at the gateway is an expected outcome the caller reacts
        // to with a refund, not an error to propagate.
        //
        // Whoever cancelled it released the hold as part of doing so, and
        // the range may since have been sold to somebody else. Returning
        // before the transition below is what keeps this payment from
        // claiming it.
        if (booking.BookingStatus == BookingStatus.Cancelled)
        {
            return false;
        }

        // Joins this transaction rather than autocommitting - see
        // HoldConfirmation.AmbientTransaction.
        bool holdIsPaid = await holdConfirmation.MarkHoldPaidAsync(holdId, cancellationToken);

        if (!holdIsPaid)
        {
            // The hold was released or expired before this payment
            // resolved, so its range is no longer this booking's to sell.
            // Reported as `false` rather than thrown: the caller's answer
            // to "this payment cannot be turned into a stay" is a refund,
            // and it already has that path for the cancelled-booking case
            // above. Throwing would instead retry an outcome that will
            // never improve.
            //
            // Reachable even under the lock: the hold is a different row
            // with its own lifecycle, and nothing in this transaction
            // stopped the expiry sweep from releasing it a moment ago.
            return false;
        }

        booking.Confirm();
        await dbContext.SaveChangesAsync(cancellationToken);

        return true;
    }
}
