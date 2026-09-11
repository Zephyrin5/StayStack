using Microsoft.EntityFrameworkCore;
using Transactions.Entities;
namespace Transactions.Contracts;

// internal, same reasoning as TransactionReversal alongside it - Bookings
// should only ever reach this through ITransactionLookup, resolved via DI.
internal class TransactionLookup(AppTransactionsDbContext dbContext) : ITransactionLookup
{
    public Task<bool> HasSucceededPaymentAsync(Guid bookingId, CancellationToken cancellationToken) =>
        // An existence check, not a SingleOrDefault: the partial unique index
        // ix_transactions_booking_id_active keeps at most one Pending or
        // Succeeded transaction per booking at a time, but a booking can
        // accumulate several finalized rows across retried payment attempts,
        // and this question is answered by whether any of them is Succeeded.
        dbContext.Transactions.AsNoTracking()
            .AnyAsync(t => t.BookingId == bookingId && t.TransactionStatus == TransactionStatus.Succeeded,
                cancellationToken);
}
