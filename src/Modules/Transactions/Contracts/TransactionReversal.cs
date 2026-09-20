using Bookings.Contracts;
using Microsoft.EntityFrameworkCore;
using SeedWork.ValueObjects;
using Transactions.Entities;
using Transactions.Exceptions;
namespace Transactions.Contracts;

// Bookings reaches this only through IPaymentReversal, resolved via DI. RefundUnusablePaymentByTransactionAsync
// is on no contract: it is Transactions' own, called by MarkTransactionSucceededHandler.
public class TransactionReversal(
    TransactionsDb dbContext,
    IBookingLookup bookingLookup,
    TimeProvider timeProvider) : IPaymentReversal
{
    public Task<decimal?> ResolveRefundAsync(Guid bookingId, CancellationToken cancellationToken) =>
        ResolveAsync(bookingId, transactionId: null, refundWithoutAnObligation: false, cancellationToken);

    /// <summary>
    ///     The refund decision for a caller that already knows which payment attempt bought nothing.
    ///     <para>
    ///         <see cref="ResolveRefundAsync"/> treats "no obligation" as "never cancelled, nothing to
    ///         settle", which is right when the trigger is a cancellation. A payment that could not
    ///         become a stay arrives from the other direction: a booking that is gone, or still Pending
    ///         with its hold released, has no obligation and never will, and is owed the whole amount.
    ///         Where an obligation does exist, RefundDecision still chooses between the policy figure
    ///         and the full amount.
    ///     </para>
    ///     <para>
    ///         Scoped to the attempt because a booking may have several transactions: the
    ///         active-transaction index constrains Pending and Succeeded to one at a time and says
    ///         nothing about the rest, so a RefundPending attempt and a Succeeded one can coexist.
    ///     </para>
    /// </summary>
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
            return await ResolveWithoutAPaymentAsync(bookingId, transactionId, cancellationToken);
        }

        // Step 2 - a refund exists, so only the obligation's bookkeeping can be outstanding.
        if (HasRecordedARefund(transaction.TransactionStatus))
        {
            await bookingLookup.MarkRefundObligationResolvedAsync(
                bookingId, timeProvider.GetUtcNow(), RefundObligationOutcome.RefundRecorded, cancellationToken);

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
        // Step 1 accepted only statuses reached through Succeeded, so SucceededAt is set.
        RefundDecision decision = RefundDecision.For(
            transaction.Amount,
            transaction.SucceededAt ?? throw new InvalidOperationException(
                $"Transaction {transaction.Id} is {transaction.TransactionStatus} with no SucceededAt."),
            obligation);

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
                bookingId, timeProvider.GetUtcNow(), RefundObligationOutcome.RefundRecorded, cancellationToken);

            return null;
        }

        // Filtered on ResolvedAt == null, so a repeat is a zero-row no-op.
        await bookingLookup.MarkRefundObligationResolvedAsync(
            bookingId, timeProvider.GetUtcNow(), RefundObligationOutcome.RefundRecorded, cancellationToken);

        return amount.Amount;
    }

    /// <summary>
    ///     No payment this obligation could be refunded from. Whether that is final or merely current
    ///     is the whole question: a cancellation stops new payments from starting (InitiateTransaction
    ///     refuses a cancelled booking), so the only payment that can still succeed is one already
    ///     Pending. With none, nothing is owed and never will be, and saying so is what stops the sweep
    ///     re-reading the row forever (docs/adr/0027).
    /// </summary>
    private async Task<decimal?> ResolveWithoutAPaymentAsync(
        Guid bookingId, Guid? transactionId, CancellationToken cancellationToken)
    {
        // Scoped to one attempt, so this says nothing about the booking's other transactions.
        if (transactionId is not null)
        {
            return null;
        }

        RefundObligationSnapshot? obligation = await bookingLookup.GetRefundObligationAsync(bookingId, cancellationToken);

        if (obligation is null || obligation.IsResolved)
        {
            return null;
        }

        bool aPaymentCanStillSucceed = await dbContext.Transactions
            .AnyAsync(t => t.BookingId == bookingId && t.TransactionStatus == TransactionStatus.Pending, cancellationToken);

        if (aPaymentCanStillSucceed)
        {
            return null;
        }

        await bookingLookup.MarkRefundObligationResolvedAsync(
            bookingId, timeProvider.GetUtcNow(), RefundObligationOutcome.NothingOwed, cancellationToken);

        return null;
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
