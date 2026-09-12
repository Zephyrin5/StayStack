using SeedWork.ValueObjects;
namespace Transactions.Contracts;

/// <summary>
///     Lets Bookings resolve whatever transaction exists for a cancelled
///     booking, without ever referencing Transactions' own entities or
///     AppTransactionsDbContext directly - same boundary reasoning as
///     Catalog.Contracts.IHoldConfirmation.
/// </summary>
public interface ITransactionReversal
{
    // ReverseTransactionAsync is gone. It was the cancellation half of a
    // decision split across two paths, and both halves are now one call -
    // ResolveRefundAsync below. Its signature had already grown a cancelledAt
    // parameter to patch the split; the parameter went with it.

    /// <summary>
    ///     The Amount of this booking's Succeeded transaction, if any -
    ///     checked *before* attempting a reversal, unlike
    ///     GetRefundSnapshotAsync below (which only finds something once a
    ///     reversal has already started). Lets a caller like
    ///     CancelBookingHandler know deterministically, at response-build
    ///     time, whether there's real money to refund - independent of
    ///     whether ReverseTransactionAsync's own outbox dispatch has
    ///     actually completed by then. Without this, a response built only
    ///     from GetRefundSnapshotAsync is indistinguishable between "there
    ///     was never anything to refund" and "there is, but the inline
    ///     dispatch attempt hasn't landed yet" - both read back null. Null
    ///     for the same reasons ReverseTransactionAsync is a no-op: no
    ///     transaction at all, still Pending, or already past Succeeded
    ///     (Failed, or already RefundPending/Refunded/RefundFailed from an
    ///     earlier reversal that already ran).
    /// </summary>
    Task<Money?> GetSucceededTransactionAmountAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     The refund already recorded against this booking, if any - what
    ///     CancelBookingHandler reports back on an idempotent re-cancel
    ///     instead of calling ReverseTransactionAsync a second time, which
    ///     would find the transaction no longer Succeeded (it already moved
    ///     to RefundPending/Refunded/RefundFailed) and return null, silently
    ///     looking like "no refund happened" for a booking where one did.
    ///     Null if this booking never had a transaction reach the refund
    ///     sub-lifecycle at all - a genuine "nothing to refund", not a
    ///     lookup failure.
    /// </summary>
    Task<TransactionRefundSnapshot?> GetRefundSnapshotAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     Decides and records the refund a cancelled booking is owed, once.
    ///     <para>
    ///         The single place the amount is chosen, replacing two paths that
    ///         each guessed whether the other would handle a case. Both inputs
    ///         are durable by the time this runs - the payment's own status and
    ///         the obligation the cancellation committed - so the decision is a
    ///         read of two committed facts rather than a prediction about a
    ///         write that may not have happened yet.
    ///     </para>
    ///     <para>
    ///         Safe to call repeatedly and from anywhere: it is a no-op unless
    ///         there is a Succeeded transaction and an unresolved obligation.
    ///         That is what lets the outbox messages be latency optimisations
    ///         over a sweep rather than the thing correctness depends on.
    ///     </para>
    ///     <para>
    ///         Returns the amount recorded, or null when there was nothing to
    ///         do.
    ///     </para>
    /// </summary>
    Task<decimal?> ResolveRefundAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     The same decision, for a caller that already knows this payment
    ///     bought nothing.
    ///     <para>
    ///         <b>Why this is not just ResolveRefundAsync.</b> That one treats
    ///         "no obligation" as "this booking was never cancelled, so there
    ///         is nothing to settle" - correct when the trigger is a
    ///         cancellation. The payment-confirmation paths reach here from a
    ///         different fact: the payment could not be turned into a stay. A
    ///         booking that is gone entirely, or one still Pending whose hold
    ///         was released underneath it, has no obligation and never will -
    ///         and is owed the whole amount.
    ///     </para>
    ///     <para>
    ///         Routing those through ResolveRefundAsync would have silently
    ///         stopped refunding them, which is the mirror image of the defect
    ///         this redesign exists to remove: a payment nobody gives back
    ///         because each side assumed the other had it.
    ///     </para>
    ///     <para>
    ///         When an obligation <em>does</em> exist, this defers to it
    ///         completely - the ordering rule still decides between the policy
    ///         figure and the full amount.
    ///     </para>
    /// </summary>
    Task<decimal?> RefundUnusablePaymentAsync(Guid bookingId, CancellationToken cancellationToken);
}

public record TransactionRefundSnapshot
{
    // Money, not bare decimals in an implied currency. The sibling
    // GetSucceededTransactionAmountAsync above already returns Money?, so
    // these were the odd ones out - and CancelBookingHandler was pairing a
    // currency back onto RefundAmount by hand to build its response.
    public required Money Amount { get; init; }
    public required Money RefundAmount { get; init; }

    /// <summary>
    ///     Whether that refund is still outstanding.
    ///     <para>
    ///         RefundAmount records what was <em>requested</em>, not what
    ///         happened: MarkRefundPending sets it, and the amount then stays
    ///         put through Refunded and RefundFailed alike. Callers were
    ///         reading its mere presence as "the refund is done" and
    ///         reporting a queued refund as settled, which is the one thing a
    ///         guest checking on their money must not be told.
    ///     </para>
    ///     <para>
    ///         A bool rather than the TransactionStatus enum, for the same
    ///         reason BookingSummary.IsPending is one: that type lives in
    ///         Transactions.Entities and this contract stays dependency-free,
    ///         and callers only need this one fact. Refunded and RefundFailed
    ///         are both false here - the first because it settled, the second
    ///         because it is a resolved failure needing intervention rather
    ///         than something still in flight.
    ///     </para>
    /// </summary>
    public required bool RefundPending { get; init; }
}
