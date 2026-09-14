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
            // Two exception types for one situation, and catching only the
            // first left a real race unhandled.
            //
            // TransactionAlreadyFinalizedException is the in-memory guard, hit
            // when this context loaded the transaction after somebody else had
            // already moved it. But two resolvers running concurrently each
            // load it as Succeeded, so both pass that guard and both issue an
            // UPDATE - and the loser is caught by the xmin concurrency token
            // instead, which surfaces as DbUpdateConcurrencyException. That
            // showed up as an intermittent failure rather than a consistent
            // one, which is the only reason it was not obvious.
            //
            // Re-read rather than assume: the point of both branches is that
            // somebody else recorded the refund, and the way to know that is to
            // ask, not to infer it from which exception arrived.
            //
            // Reload this one entity rather than ChangeTracker.Clear(). This
            // context is scoped, and when the caller is
            // TransactionsOutboxDispatcher the very same instance is tracking
            // the OutboxMessage that dispatcher is in the middle of processing.
            // Clearing detached it, so the ProcessedAt assigned afterwards went
            // to a detached entity, SaveChangesAsync wrote nothing, and the
            // dispatcher reported success over a message still pending. It
            // self-healed on redelivery, which is worse rather than better: the
            // reported outcome and the persisted state disagreed and nothing
            // said so.
            //
            // Reload rather than Detach because the check below wants this
            // row's current state anyway.
            await dbContext.Entry(transaction).ReloadAsync(cancellationToken);

            // The same question as step 2 above, asked the same way. The two
            // used to disagree: step 2 accepted RefundPending only, while this
            // was written as != Succeeded - which also matches Failed, where no
            // refund was written at all.
            bool refundExists = HasRecordedARefund(transaction.TransactionStatus);

            if (!refundExists)
            {
                // Nothing recorded it after all, so this is a genuine failure
                // rather than a lost race. Leave the obligation unresolved and
                // let the sweep try again.
                throw;
            }

            // Someone else recorded the refund between the read above and this
            // write. The refund exists, so finish the bookkeeping rather than
            // abandoning it - the old comment here claimed the obligation was
            // "still marked below", which the return made false, and the row
            // was left unresolved for the sweep to retry forever.
            await bookingLookup.MarkRefundObligationResolvedAsync(
                bookingId, timeProvider.GetUtcNow(), cancellationToken);

            return null;
        }

        // Two commits, and this is the second. They stay non-atomic, which is
        // fine now that neither can veto the other: a crash in between leaves
        // the obligation unresolved and a later run takes step 2 above, while a
        // marker that committed without its refund is simply ignored.
        //
        // MarkRefundObligationResolvedAsync filters ResolvedAt == null in its
        // own ExecuteUpdate, so every repeat is a zero-row no-op.
        await bookingLookup.MarkRefundObligationResolvedAsync(
            bookingId, timeProvider.GetUtcNow(), cancellationToken);

        return amount.Amount;
    }

    /// <summary>
    ///     Whether this status means a refund has been recorded against the
    ///     payment - in any state, settled or not.
    ///     <para>
    ///         One definition, because two places need it and they had drifted.
    ///         The repair step accepted RefundPending alone, so a refund that
    ///         reached Refunded or RefundFailed before its marker was written
    ///         became invisible: the main query matched nothing and the
    ///         obligation never settled, polling forever behind its backoff.
    ///         The concurrency catch asked the looser <c>!= Succeeded</c>, which
    ///         wrongly counts Failed - a payment that produced no refund at all.
    ///     </para>
    /// </summary>
    private static bool HasRecordedARefund(TransactionStatus status) =>
        status is TransactionStatus.RefundPending
            or TransactionStatus.Refunded
            or TransactionStatus.RefundFailed;

    public async Task<PaymentStateSnapshot?> GetPaymentStateAsync(
        Guid bookingId, CancellationToken cancellationToken)
    {
        // One query, one ordering, one answer. The pair of reads it replaces
        // could straddle a Succeeded -> RefundPending transition and report a
        // state that never existed.
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

    public async Task<Money?> GetSucceededTransactionAmountAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // Same query ReverseTransactionAsync itself uses to decide whether
        // there's anything to do - exposed as a standalone read so a caller
        // can ask the same question before dispatch, not just infer it from
        // whatever ReverseTransactionAsync eventually did.
        // FirstOrDefault, ordered. The active-transaction index keeps at most
        // one Succeeded row per booking at a time, so this is one row today -
        // but that index is not a uniqueness guarantee for anything else, and
        // every sibling query here learned the same lesson the hard way.
        Transaction? transaction = await dbContext.Transactions.AsNoTracking()
            .Where(t => t.BookingId == bookingId && t.TransactionStatus == TransactionStatus.Succeeded)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

        return transaction?.Amount;
    }

    public async Task<TransactionRefundSnapshot?> GetRefundSnapshotAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        // Most recent refund, not "the" refund.
        //
        // This used to be a SingleOrDefaultAsync, justified on the grounds that
        // MarkRefundPending is only reachable from Succeeded and never
        // reversible, so at most one transaction per booking can carry a refund
        // amount. Every clause of that is true of a single payment attempt and
        // none of it bounds a booking: a booking can accumulate several
        // transactions, the active-transaction index constrains only Pending
        // and Succeeded, and two refunded attempts are a state the schema
        // permits. It threw when they occurred.
        //
        // Ordered by SucceededAt so the answer is deterministic rather than
        // whatever the planner returned first. Materialize first, map after -
        // see docs/adr/0006, applied to Amount's ComplexProperty mapping the
        // same way BookingLookup.GetBookingAsync already does for TotalPrice.
        Transaction? transaction = await dbContext.Transactions.AsNoTracking()
            .Where(
                // EF.Property, because RefundAmount is now computed from the
                // backing field and Amount's currency, and a computed property
                // has no SQL translation.
                t => t.BookingId == bookingId
                     && EF.Property<decimal?>(t, Transaction.RefundAmountField) != null)
            .OrderByDescending(t => t.Id)
            .FirstOrDefaultAsync(cancellationToken);

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
