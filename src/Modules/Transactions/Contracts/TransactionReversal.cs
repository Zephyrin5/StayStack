using Bookings.Contracts;
using Microsoft.EntityFrameworkCore;
using SeedWork.ValueObjects;
using Transactions.Entities;
using Transactions.Exceptions;
namespace Transactions.Contracts;

// internal, same reasoning as Catalog.Contracts.HoldConfirmation - Bookings
// should only ever reach this through ITransactionReversal, resolved via DI.
internal class TransactionReversal(
    AppTransactionsDbContext dbContext,
    IBookingLookup bookingLookup,
    TimeProvider timeProvider) : ITransactionReversal
{
    public async Task<decimal?> ReverseTransactionAsync(
        Guid bookingId, Money refundAmount, DateTimeOffset? cancelledAt, CancellationToken cancellationToken)
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

        // Declines when the payment landed after the cancellation: that payment
        // bought nothing, so the whole amount is owed rather than a policy
        // percentage of it, and the confirmation path is the one holding that
        // figure. Returning null here is the same "nothing to reverse" answer
        // the caller already handles.
        if (!transaction.RefundOwedIsThisPathsToWrite(RefundCause.GuestCancellation, cancelledAt))
        {
            return null;
        }

        try
        {
            transaction.MarkRefundPending(refundAmount, RefundCause.GuestCancellation);
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

    public async Task<decimal?> ResolveRefundAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // Step 1 - is there money to give back? Not Succeeded covers every
        // "nothing to do yet" case at once: still Pending at the gateway,
        // Failed, or already past Succeeded because a refund was decided
        // earlier.
        Transaction? transaction = await dbContext.Transactions
            .SingleOrDefaultAsync(
                t => t.BookingId == bookingId && t.TransactionStatus == TransactionStatus.Succeeded,
                cancellationToken);

        if (transaction is null)
        {
            return null;
        }

        // Step 2 - was it cancelled? The obligation is the answer, not the
        // booking's own CancelledAt. It is written in the cancellation's own
        // transaction, so it is visible if and only if the cancellation
        // committed; reading the booking instead could see a null for a
        // cancellation already in flight and conclude the payment came second.
        RefundObligationSnapshot? obligation =
            await bookingLookup.GetRefundObligationAsync(bookingId, cancellationToken);

        if (obligation is null || obligation.IsResolved)
        {
            return null;
        }

        // Step 3 - how much. Both inputs are committed facts by now, which is
        // the entire point of resolving here rather than at either writer.
        //
        // Paid, then cancelled: the guest bought a stay and gave it up, so the
        // cancellation policy applies. Cancelled, then paid: that payment
        // bought nothing, so all of it goes back.
        //
        // A null SucceededAt is a row from before that column existed. It takes
        // the policy refund - unknown history, the conservative answer - and
        // never "somebody else owns this", which is the inference that produced
        // refunds nobody wrote.
        bool paidAfterTheCancellation =
            transaction.SucceededAt is { } succeeded && succeeded > obligation.CancelledAt;

        Money amount = paidAfterTheCancellation ? transaction.Amount : obligation.PolicyRefundAmount;

        RefundCause cause = paidAfterTheCancellation
            ? RefundCause.PaymentUnusable
            : obligation.Cause == BookingCancellationCause.Expiry
                ? RefundCause.BookingExpired
                : RefundCause.GuestCancellation;

        try
        {
            transaction.MarkRefundPending(amount, cause);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (TransactionAlreadyFinalizedException)
        {
            // Something resolved this transaction between the read above and
            // this write. Whichever side won is already correct; the obligation
            // is still marked below so the sweep stops revisiting it.
            return null;
        }

        // Two commits, and this is the second. A crash in between leaves the
        // obligation unresolved and the backstop job runs this again -
        // harmlessly, because MarkRefundPending is a no-op once an amount is
        // recorded. The reverse order would be the unsafe one: an obligation
        // marked resolved with no refund behind it is invisible to every
        // retry there is.
        await bookingLookup.MarkRefundObligationResolvedAsync(
            bookingId, timeProvider.GetUtcNow(), cancellationToken);

        return amount.Amount;
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
            : new TransactionRefundSnapshot
            {
                Amount = transaction.Amount,
                RefundAmount = transaction.RefundAmount!.Value,
                // The outcome, read from the status rather than inferred from
                // the amount's presence. The amount survives into Refunded
                // and RefundFailed unchanged, so it can only ever say a
                // refund was requested.
                RefundPending = transaction.TransactionStatus == TransactionStatus.RefundPending
            };
    }
}
