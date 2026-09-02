using Microsoft.EntityFrameworkCore;
using SeedWork.ValueObjects;
using Transactions.Entities;
using Transactions.Exceptions;
namespace Transactions.Contracts;

// internal, same reasoning as Catalog.Contracts.HoldConfirmation - Bookings
// should only ever reach this through ITransactionReversal, resolved via DI.
internal class TransactionReversal(AppTransactionsDbContext dbContext) : ITransactionReversal
{
    public async Task<decimal?> ReverseTransactionAsync(Guid bookingId, Money refundAmount, CancellationToken cancellationToken)
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
            return null;
        }

        try
        {
            transaction.MarkRefundPending(refundAmount);
            await dbContext.SaveChangesAsync(cancellationToken);
            return refundAmount.Amount;
        }
        catch (TransactionAlreadyFinalizedException)
        {
            // Lost a race to a concurrent resolution (e.g.
            // MarkTransactionSucceededHandler reaching the same
            // conclusion from its own side at the same moment) - whichever
            // side won is already correct, nothing left to do here.
            return null;
        }
    }

    public async Task<Money?> GetSucceededTransactionAmountAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // Same query ReverseTransactionAsync itself uses to decide whether
        // there's anything to do - exposed as a standalone read so a caller
        // can ask the same question before dispatch, not just infer it from
        // whatever ReverseTransactionAsync eventually did.
        Transaction? transaction = await dbContext.Transactions.AsNoTracking()
            .SingleOrDefaultAsync(t => t.BookingId == bookingId && t.TransactionStatus == TransactionStatus.Succeeded, cancellationToken);

        return transaction?.Amount;
    }

    public async Task<TransactionRefundSnapshot?> GetRefundSnapshotAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // RefundAmount is only ever set by MarkRefundPending, which is only
        // reachable from Succeeded and never reversible - at most one
        // transaction per booking can ever have a non-null RefundAmount, so
        // this is safe as a SingleOrDefaultAsync despite a booking
        // potentially having more than one Transaction row across retried
        // payment attempts (the partial unique index only constrains
        // Pending/Succeeded to one at a time, not history). Materialize
        // first, map after - see docs/adr/0006, applied to Amount's
        // ComplexProperty mapping the same way BookingLookup.GetBookingAsync
        // already does for TotalPrice.
        Transaction? transaction = await dbContext.Transactions.AsNoTracking()
            .SingleOrDefaultAsync(
                // EF.Property, because RefundAmount is now computed from the
                // backing field and Amount's currency, and a computed property
                // has no SQL translation.
                t => t.BookingId == bookingId
                     && EF.Property<decimal?>(t, Transaction.RefundAmountField) != null,
                cancellationToken);

        return transaction is null
            ? null
            : new TransactionRefundSnapshot { Amount = transaction.Amount, RefundAmount = transaction.RefundAmount!.Value };
    }
}
