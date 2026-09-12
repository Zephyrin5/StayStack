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
        ResolveAsync(bookingId, refundWithoutAnObligation: false, cancellationToken);

    public Task<decimal?> RefundUnusablePaymentAsync(Guid bookingId, CancellationToken cancellationToken) =>
        ResolveAsync(bookingId, refundWithoutAnObligation: true, cancellationToken);

    private async Task<decimal?> ResolveAsync(
        Guid bookingId, bool refundWithoutAnObligation, CancellationToken cancellationToken)
    {
        // Step 1 - what does the ledger say? RefundPending is included
        // deliberately: it is the only durable proof that a refund was
        // recorded, and this method has to be able to tell "already done" from
        // "not done yet" without trusting a flag that commits somewhere else.
        //
        // Everything excluded here is genuinely nothing to do: still Pending at
        // the gateway, Failed, or already past the refund sub-lifecycle.
        Transaction? transaction = await dbContext.Transactions
            .SingleOrDefaultAsync(
                t => t.BookingId == bookingId
                     && (t.TransactionStatus == TransactionStatus.Succeeded
                         || t.TransactionStatus == TransactionStatus.RefundPending),
                cancellationToken);

        if (transaction is null)
        {
            return null;
        }

        // Step 2 - the refund is already recorded, so only the bookkeeping can
        // still be outstanding. Finish it and stop.
        //
        // This is the half of the fix that closes the *mirror* failure: the
        // refund commits, the process dies before the marker, and the old code
        // - which queried for Succeeded alone - then found nothing on every
        // later run and returned at step 1. The obligation stayed unresolved
        // forever and the sweep re-processed it every cycle, permanently.
        if (transaction.TransactionStatus == TransactionStatus.RefundPending)
        {
            await bookingLookup.MarkRefundObligationResolvedAsync(
                bookingId, timeProvider.GetUtcNow(), cancellationToken);

            return null;
        }

        // Step 3 - was it cancelled? The obligation is the answer, not the
        // booking's own CancelledAt. It is written in the cancellation's own
        // transaction, so it is visible if and only if the cancellation
        // committed; reading the booking instead could see a null for a
        // cancellation already in flight and conclude the payment came second.
        RefundObligationSnapshot? obligation =
            await bookingLookup.GetRefundObligationAsync(bookingId, cancellationToken);

        // Deliberately NO early return on obligation.IsResolved, and this is
        // the other half of the fix.
        //
        // The marker and the refund commit separately, and which one is inside
        // a transaction depends on which dispatcher called this - the two are
        // mirror images. OutboxDispatcherBase runs TryHandleAsync *inside* its
        // claim transaction, so from TransactionsOutboxDispatcher the refund
        // write joins that uncommitted transaction while the Bookings marker
        // autocommits on its own connection; from BookingsOutboxDispatcher it
        // is the reverse.
        //
        // So a dispatcher transaction that fails after this ran could leave the
        // obligation marked resolved with the refund rolled back. Treating that
        // flag as authority made the retry return here, the dispatcher mark the
        // message processed, and the sweep skip the row on ResolvedAt != null -
        // a refund silently lost with every mechanism reporting success.
        //
        // The transaction's own status is the authority. The flag is bookkeeping
        // that lets the sweep skip cheaply when it agrees, and nothing more.
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
            dbContext.ChangeTracker.Clear();

            bool refundExists = await dbContext.Transactions.AsNoTracking()
                .AnyAsync(
                    t => t.BookingId == bookingId && t.TransactionStatus != TransactionStatus.Succeeded,
                    cancellationToken);

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
