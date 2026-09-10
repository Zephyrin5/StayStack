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

        // The hold transition and the confirmation are now one transaction,
        // which retires the ordering argument that used to live here.
        //
        // They were two commits on two DbContexts against two schemas - a
        // compensating pair (docs/adr/0003) - so one landed first and a crash
        // could fall between. Confirming the booking first left the worst
        // possible remainder: a Confirmed booking whose inventory had been
        // released to someone else, unrepairable because the range might
        // already be sold. Marking the hold paid first inverted that into a
        // sold hold against a still-Pending booking, recoverable in both
        // directions. That inversion was the right call while the halves were
        // separable; they are not separable any more, so neither remainder is
        // reachable.
        //
        // The idempotent predicate MarkHoldPaidAsync uses - accepting a hold
        // already in 'booked' as well as 'pending_payment' - deliberately
        // stays. The outbox can still deliver this message more than once,
        // and a redelivery arriving after a committed confirmation must
        // remain a no-op rather than a failure.
        return await ConfirmUnderRowLockAsync(bookingId, holdId, cancellationToken);
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
    ///         The hold's 'pending_payment' -> 'booked' transition happens
    ///         inside this same transaction and inside this same lock, so a
    ///         payment either sells the range and confirms the stay or does
    ///         neither. It also means the cancelled-booking check below now
    ///         runs <em>before</em> the hold is touched, where it used to run
    ///         after: a payment resolving against a booking somebody already
    ///         cancelled no longer marks that booking's hold sold on its way
    ///         to reporting the refund.
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
    private async Task<bool> ConfirmUnderRowLockAsync(Guid bookingId, Guid holdId, CancellationToken cancellationToken)
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
            // Whoever cancelled it released the hold as part of doing so, and
            // the range may since have been sold to somebody else. Returning
            // before the transition below is what keeps this payment from
            // claiming it.
            if (booking.BookingStatus == BookingStatus.Cancelled)
            {
                await transaction.RollbackAsync(cancellationToken);
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
