using Availability.Contracts;
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

        // The hold first, the booking second, and the order is load-bearing.
        //
        // These are two commits on two DbContexts against two schemas - a
        // compensating pair (docs/adr/0003), never one transaction - so one
        // of them lands first and a crash can fall between. The question is
        // only which half is safe to have alone.
        //
        // Confirming the booking first, which is what this did originally,
        // leaves the worst possible remainder if the hold transition then
        // fails: a Confirmed booking whose inventory has been released to
        // someone else. Nothing can repair that - the range may already be
        // sold - and because it threw, the outbox retried it forever, each
        // attempt re-reading a booking that was already Confirmed and
        // failing the same way until it dead-lettered into an hourly sweep.
        // Money taken, nothing sold, and loud in metrics rather than fixed.
        //
        // Marking the hold paid first inverts that. If it fails, nothing has
        // committed anywhere and the booking is untouched. If the confirm
        // below then fails, the outbox retries and this call no-ops, because
        // MarkHoldPaidAsync accepts an already-'booked' hold. The only
        // remainder is a sold hold against a still-Pending booking, which is
        // recoverable in both directions: a retry finishes it, and if the
        // payment window lapses first, ExpireUnpaidBookingsJob cancels the
        // booking and releases the hold, which drives this method's `false`
        // path and the refund that goes with it. A compensated outcome
        // instead of an uncompensatable one.
        bool holdIsPaid = await holdConfirmation.MarkHoldPaidAsync(holdId, cancellationToken);

        if (!holdIsPaid)
        {
            // The hold was released or expired before this payment resolved,
            // so its range is no longer this booking's to sell. Reported as
            // `false` rather than thrown: the caller's response to "this
            // payment cannot be turned into a stay" is a refund, and it
            // already has that path for the cancelled-booking case. Throwing
            // would instead retry an outcome that will never improve.
            return false;
        }

        return await ConfirmUnderRowLockAsync(bookingId, cancellationToken);
    }

    /// <summary>
    ///     Transitions Pending -> Confirmed while holding the booking's row
    ///     lock, so this path arbitrates for the row the same way
    ///     ExpireUnpaidBookingsJob does.
    ///     <para>
    ///         Without it the two sides were asymmetric: expiry locked the
    ///         row with FOR UPDATE and re-checked under the lock, while
    ///         payment did an unlocked read, a status check against that
    ///         stale read, and an unconditional EF update. Booking carries no
    ///         concurrency token, so the generated UPDATE keyed on Id alone
    ///         and could not fail. The interleaving that produced was real:
    ///         payment reads Pending, expiry locks the row, releases the hold
    ///         and commits Cancelled, then payment's UPDATE - which had been
    ///         blocking on that lock - proceeds and overwrites Cancelled back
    ///         to Confirmed. A lost update, leaving a Confirmed booking whose
    ///         inventory had just been handed back.
    ///     </para>
    ///     <para>
    ///         FOR UPDATE, not FOR UPDATE SKIP LOCKED as the expiry job uses.
    ///         Their needs are opposite: a sweep should step over a row
    ///         someone else is working on and revisit it next run, while this
    ///         has a payment in hand and must wait to see the committed
    ///         outcome - which is exactly how it learns the booking was
    ///         cancelled and a refund is owed.
    ///     </para>
    /// </summary>
    private async Task<bool> ConfirmUnderRowLockAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

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
                    transaction.GetDbTransaction(),
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
            // The hold above is already 'booked' in this case, and is left
            // that way deliberately: whoever cancelled the booking released
            // it as part of doing so, and re-releasing it here could hand
            // back a range that has since been sold to somebody else.
            if (booking.BookingStatus == BookingStatus.Cancelled)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            booking.Confirm();
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return true;
        });
    }
}
