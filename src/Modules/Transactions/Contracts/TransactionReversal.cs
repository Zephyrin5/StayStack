using Bookings.Contracts;
using Microsoft.EntityFrameworkCore;
using SeedWork.ValueObjects;
using Transactions.Entities;
using Transactions.Exceptions;
namespace Transactions.Contracts;

// Bookings reaches this only through ITransactionReversal, resolved via DI.
internal class TransactionReversal(
    AppTransactionsDbContext dbContext,
    IBookingLookup bookingLookup,
    TimeProvider timeProvider) : ITransactionReversal
{
    public Task<decimal?> ResolveRefundAsync(Guid bookingId, CancellationToken cancellationToken) =>
        ResolveAsync(bookingId, transactionId: null, refundWithoutAnObligation: false, cancellationToken);

    public Task<decimal?> RefundUnusablePaymentAsync(Guid bookingId, CancellationToken cancellationToken) =>
        ResolveAsync(bookingId, transactionId: null, refundWithoutAnObligation: true, cancellationToken);

    public async Task<decimal?> RefundUnusablePaymentByTransactionAsync(
        Guid transactionId, CancellationToken cancellationToken)
    {
        // The booking id comes from the row rather than the caller, so the two
        // can never disagree - and the resolve below is still scoped to this
        // one attempt.
        Guid? bookingId = await dbContext.Transactions.AsNoTracking()
            .Where(t => t.Id == transactionId)
            .Select(t => (Guid?)t.BookingId)
            .SingleOrDefaultAsync(cancellationToken);

        return bookingId is null
            ? null
            : await ResolveAsync(bookingId.Value, transactionId, refundWithoutAnObligation: true, cancellationToken);
    }

    private async Task<decimal?> ResolveAsync(
        Guid bookingId, Guid? transactionId, bool refundWithoutAnObligation, CancellationToken cancellationToken)
    {
        // Step 1 - the ledger. The transaction's own status is the authority on
        // whether a refund exists; the obligation's ResolvedAt is bookkeeping
        // committed separately (docs/adr/0025). So the query accepts every status
        // a recorded refund can be in, not only Succeeded: step 2 needs to see
        // "already refunded" to finish the bookkeeping.
        //
        // First matching row, never SingleOrDefault: a booking can have several
        // transactions, and the active index constrains only Pending and
        // Succeeded. Succeeded sorts first, so a booking-wide call refunds the
        // outstanding payment. Ordered by Id (version-7, creation-ordered, never
        // null) rather than SucceededAt, which is null on older rows and which
        // SQLite - the unit tests' provider - cannot order.
        IQueryable<Transaction> candidates = dbContext.Transactions
            .Where(t => t.BookingId == bookingId
                        && (t.TransactionStatus == TransactionStatus.Succeeded
                            || t.TransactionStatus == TransactionStatus.RefundPending
                            || t.TransactionStatus == TransactionStatus.Refunded
                            || t.TransactionStatus == TransactionStatus.RefundFailed));

        if (transactionId is { } attempt)
        {
            candidates = candidates.Where(t => t.Id == attempt);
        }

        Transaction? transaction = await candidates
            .OrderByDescending(t => t.TransactionStatus == TransactionStatus.Succeeded)
            .ThenByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (transaction is null)
        {
            return null;
        }

        // Step 2 - a refund is already recorded, so only the obligation's
        // bookkeeping can be outstanding. Finish it; a crash between the refund
        // commit and the marker lands here on the next run.
        if (HasRecordedARefund(transaction.TransactionStatus))
        {
            await bookingLookup.MarkRefundObligationResolvedAsync(
                bookingId, timeProvider.GetUtcNow(), cancellationToken);

            return null;
        }

        // Step 3 - was it cancelled? The obligation answers that, because it is
        // written in the cancellation's own transaction: visible if and only if
        // the cancellation committed. The booking's CancelledAt, read through
        // another module, could be null for a cancellation still in flight.
        RefundObligationSnapshot? obligation =
            await bookingLookup.GetRefundObligationAsync(bookingId, cancellationToken);

        // Deliberately no early return on obligation.IsResolved, and adding one
        // loses refunds. The refund and the marker commit in different
        // transactions, and which one is inside the caller's transaction
        // depends on the dispatcher (OutboxDispatcherBase runs handlers inside
        // its claim transaction). A claim that rolls back after this ran can
        // leave the marker set with no refund behind it. The transaction status,
        // read in step 1, is the authority; the marker only lets the sweep skip.
        if (obligation is null)
        {
            // No cancellation explains this payment. For a caller triggered by
            // one, that means there is nothing to settle. For a caller that
            // already established the payment bought nothing - a booking gone
            // entirely, or a hold released underneath a still-Pending one - it
            // means the whole amount is owed and no obligation is ever coming.
            if (!refundWithoutAnObligation)
            {
                return null;
            }

            transaction.MarkRefundPending(transaction.Amount, RefundCause.PaymentUnusable);
            await dbContext.SaveChangesAsync(cancellationToken);

            return transaction.Amount.Amount;
        }

        // Step 4 - how much, from two committed facts. RefundDecision owns the
        // rule; CancelBookingHandler reports pending refunds from the same call.
        RefundDecision decision = RefundDecision.For(transaction.Amount, transaction.SucceededAt, obligation);

        Money amount = decision.Amount;

        RefundCause cause = decision.PaidAfterTheCancellation
            ? RefundCause.PaymentUnusable
            : obligation.Cause == BookingCancellationCause.Expiry
                ? RefundCause.BookingExpired
                : RefundCause.GuestCancellation;

        try
        {
            transaction.MarkRefundPending(amount, cause);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is TransactionAlreadyFinalizedException or DbUpdateConcurrencyException)
        {
            // Somebody else may have recorded the refund. The in-memory guard
            // throws when this context loaded the transaction already moved; two
            // resolvers loading it concurrently both pass that guard, and the
            // loser's UPDATE fails the xmin concurrency token instead. Either way
            // the row is re-read rather than the exception trusted.
            //
            // Reload this one entity, not ChangeTracker.Clear(): when the caller
            // is TransactionsOutboxDispatcher, this scoped context also tracks the
            // OutboxMessage being processed. Clearing would detach it, its
            // ProcessedAt would never be saved, and the dispatcher would report
            // success over a message still pending.
            await dbContext.Entry(transaction).ReloadAsync(cancellationToken);

            bool refundExists = HasRecordedARefund(transaction.TransactionStatus);

            if (!refundExists)
            {
                // Nothing recorded it after all, so this is a genuine failure
                // rather than a lost race. Leave the obligation unresolved and
                // let the sweep try again.
                throw;
            }

            // Someone else recorded the refund between the read above and this
            // write, so finish the bookkeeping.
            await bookingLookup.MarkRefundObligationResolvedAsync(
                bookingId, timeProvider.GetUtcNow(), cancellationToken);

            return null;
        }

        // Two commits, and this is the second. A crash in between leaves the
        // obligation unresolved and a later run takes step 2 above; a marker
        // committed without its refund is ignored, because step 1 reads the
        // transaction status.
        //
        // MarkRefundObligationResolvedAsync filters ResolvedAt == null in its
        // own ExecuteUpdate, so every repeat is a zero-row no-op.
        await bookingLookup.MarkRefundObligationResolvedAsync(
            bookingId, timeProvider.GetUtcNow(), cancellationToken);

        return amount.Amount;
    }

    /// <summary>
    ///     Whether this status means a refund has been recorded against the
    ///     payment - in any state, settled or not. Failed is excluded: that
    ///     payment produced no refund. Step 2 and the concurrency catch share it,
    ///     so a refund that reached Refunded or RefundFailed before its marker
    ///     was written still settles the obligation.
    /// </summary>
    private static bool HasRecordedARefund(TransactionStatus status) =>
        status is TransactionStatus.RefundPending
            or TransactionStatus.Refunded
            or TransactionStatus.RefundFailed;

    public async Task<PaymentStateSnapshot?> GetPaymentStateAsync(
        Guid bookingId, CancellationToken cancellationToken)
    {
        // One query, so the answer cannot straddle a Succeeded -> RefundPending
        // transition.
        //
        // Succeeded first, then newest, matching the resolver: the payment
        // actually outstanding is the one worth describing, and a refunded
        // sibling is history.
        Transaction? transaction = await dbContext.Transactions.AsNoTracking()
            .Where(t => t.BookingId == bookingId
                        && t.TransactionStatus != TransactionStatus.Pending
                        && t.TransactionStatus != TransactionStatus.Failed)
            .OrderByDescending(t => t.TransactionStatus == TransactionStatus.Succeeded)
            .ThenByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return transaction is null
            ? null
            : new PaymentStateSnapshot
            {
                Amount = transaction.Amount,
                RefundAmount = transaction.RefundAmount,
                RefundStatus = transaction.TransactionStatus switch
                {
                    TransactionStatus.RefundPending => RefundStatus.Pending,
                    TransactionStatus.Refunded => RefundStatus.Refunded,
                    TransactionStatus.RefundFailed => RefundStatus.Failed,
                    _ => RefundStatus.None
                },
                AwaitingRefund = transaction.TransactionStatus == TransactionStatus.Succeeded,
                SucceededAt = transaction.SucceededAt
            };
    }
}
