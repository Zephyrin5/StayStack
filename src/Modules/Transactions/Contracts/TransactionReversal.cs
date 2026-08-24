using Microsoft.EntityFrameworkCore;
using Transactions.Entities;
using Transactions.Exceptions;
namespace Transactions.Contracts;

// internal, same reasoning as Catalog.Contracts.HoldConfirmation - Bookings
// should only ever reach this through ITransactionReversal, resolved via DI.
internal class TransactionReversal(AppTransactionsDbContext dbContext) : ITransactionReversal
{
    public async Task ReverseTransactionAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // Only a Succeeded transaction needs anything done here - money
        // was actually collected, so it needs reversing. A Pending one is
        // deliberately left untouched: we don't yet know what the gateway
        // will do with it, and guessing Failed here would be wrong if it
        // later succeeds anyway. That case is instead handled on the
        // success side - see MarkTransactionSucceededHandler, which checks
        // whether the booking it's confirming is still around before
        // treating a success as good news.
        Transaction? transaction = await dbContext.Transactions
            .SingleOrDefaultAsync(t => t.BookingId == bookingId && t.TransactionStatus == TransactionStatus.Succeeded, cancellationToken);

        if (transaction is null)
        {
            return;
        }

        try
        {
            transaction.MarkRefundPending();
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (TransactionAlreadyFinalizedException)
        {
            // Lost a race to a concurrent resolution (e.g.
            // MarkTransactionSucceededHandler reaching the same
            // conclusion from its own side at the same moment) - whichever
            // side won is already correct, nothing left to do here.
        }
    }
}
