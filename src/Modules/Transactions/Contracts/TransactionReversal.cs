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
        // From the row, not the caller, so the two cannot disagree.
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
        // The refund and the obligation's ResolvedAt commit together in the caller's scope. Both record
        // a decision locally; no money moves here (docs/adr/0027).
        if (dbContext.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException(
                $"{nameof(TransactionReversal)} must run inside the caller's atomic scope: a refund committed apart " +
                "from its obligation's marker, or from the cancellation it answers, can disagree with both.");
        }

        // Step 1 - the ledger, which is the authority on whether a refund exists, so every recorded
        // refund status counts and not only Succeeded.
        //
        // First match, never SingleOrDefault: a booking may have several transactions, and the active
        // index constrains only Pending and Succeeded. Succeeded first, then newest by id.
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

        // Step 2 - a refund exists, so only the obligation's bookkeeping can be outstanding.
        if (HasRecordedARefund(transaction.TransactionStatus))
        {
            await bookingLookup.MarkRefundObligationResolvedAsync(
                bookingId, timeProvider.GetUtcNow(), cancellationToken);

            return null;
        }

        // Step 3 - the obligation, not the booking's CancelledAt: it is visible only once the
        // cancellation committed.
        RefundObligationSnapshot? obligation =
            await bookingLookup.GetRefundObligationAsync(bookingId, cancellationToken);

        // No early return on IsResolved: the transaction status is the authority, the marker only lets
        // the sweep skip.
        if (obligation is null)
        {
            // No cancellation explains this payment: nothing to settle, unless the caller already
            // established the payment bought nothing, in which case the whole amount is owed.
            if (!refundWithoutAnObligation)
            {
                return null;
            }

            transaction.MarkRefundPending(transaction.Amount, RefundCause.PaymentUnusable);
            await dbContext.SaveChangesAsync(cancellationToken);

            return transaction.Amount.Amount;
        }

        // Step 4 - how much, from two committed facts. RefundDecision owns the rule, and
        // CancelBookingHandler reports a pending refund from the same call.
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
            // Somebody else may have recorded the refund: the in-memory guard throws, or a stale xmin
            // matches no row. Either way the row is re-read rather than the exception trusted.
            // Reload one entity, not ChangeTracker.Clear(): the caller's scope tracks others here.
            await dbContext.Entry(transaction).ReloadAsync(cancellationToken);

            bool refundExists = HasRecordedARefund(transaction.TransactionStatus);

            // Nothing recorded it, so this is a real failure; the sweep retries the obligation.
            if (!refundExists)
            {
                throw;
            }

            await bookingLookup.MarkRefundObligationResolvedAsync(
                bookingId, timeProvider.GetUtcNow(), cancellationToken);

            return null;
        }

        // Filtered on ResolvedAt == null, so a repeat is a zero-row no-op.
        await bookingLookup.MarkRefundObligationResolvedAsync(
            bookingId, timeProvider.GetUtcNow(), cancellationToken);

        return amount.Amount;
    }

    /// <summary>Whether a refund has been recorded, settled or not. Failed produced no refund.</summary>
    private static bool HasRecordedARefund(TransactionStatus status) =>
        status is TransactionStatus.RefundPending
            or TransactionStatus.Refunded
            or TransactionStatus.RefundFailed;

    public async Task<PaymentStateSnapshot?> GetPaymentStateAsync(
        Guid bookingId, CancellationToken cancellationToken)
    {
        // One query, so the answer cannot straddle a Succeeded -> RefundPending transition. Succeeded
        // first, then newest, matching the resolver: the outstanding payment is the one to describe.
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
