using SeedWork.ValueObjects;
namespace Transactions.Contracts;

/// <summary>
///     Lets Bookings resolve the payment behind a cancelled booking without
///     referencing Transactions' entities or AppTransactionsDbContext.
/// </summary>
public interface ITransactionReversal
{
    /// <summary>
    ///     The Amount of this booking's Succeeded transaction, if any. Null when
    ///     there is no transaction, it is still Pending, or it has already moved
    ///     past Succeeded.
    /// </summary>
    Task<Money?> GetSucceededTransactionAmountAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     The most recent refund recorded against this booking, if any. Null
    ///     when no transaction reached the refund sub-lifecycle - nothing to
    ///     refund, not a lookup failure.
    /// </summary>
    Task<TransactionRefundSnapshot?> GetRefundSnapshotAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     Everything a caller needs to describe this booking's payment, read
    ///     once.
    ///     <para>
    ///         One read because the state moves: a dispatcher or the sweep can take
    ///         a payment Succeeded -> RefundPending between two reads, and then a
    ///         refund lookup followed by a succeeded-amount lookup sees neither.
    ///         Null means nothing succeeded and nothing was refunded.
    ///     </para>
    /// </summary>
    Task<PaymentStateSnapshot?> GetPaymentStateAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     Decides and records the refund a cancelled booking is owed, once.
    ///     <para>
    ///         The single place the amount is chosen (docs/adr/0027). Both inputs
    ///         are durable by the time this runs - the payment's own status and
    ///         the obligation the cancellation committed - so the decision reads
    ///         two committed facts rather than predicting a write.
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
    ///         ResolveRefundAsync treats "no obligation" as "never cancelled,
    ///         nothing to settle", which is right when the trigger is a
    ///         cancellation. The payment-confirmation paths arrive from a
    ///         different fact: the payment could not become a stay. A booking
    ///         that is gone, or still Pending with its hold released, has no
    ///         obligation and never will, and is owed the whole amount. Routed
    ///         through ResolveRefundAsync, those payments would never be refunded.
    ///     </para>
    ///     <para>
    ///         When an obligation exists, this defers to it: RefundDecision
    ///         chooses between the policy figure and the full amount.
    ///     </para>
    /// </summary>
    Task<decimal?> RefundUnusablePaymentAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     The same as <see cref="RefundUnusablePaymentAsync"/>, for a caller
    ///     that knows <em>which</em> payment attempt it is talking about.
    ///     <para>
    ///         A booking may have several transactions. The active-transaction
    ///         index constrains Pending and Succeeded to one at a time and says
    ///         nothing about the rest, so a RefundPending attempt and a
    ///         Succeeded one can coexist; scoping to the attempt keeps a path that
    ///         already knows it from choosing between them.
    ///     </para>
    /// </summary>
    Task<decimal?> RefundUnusablePaymentByTransactionAsync(
        Guid transactionId, CancellationToken cancellationToken);
}

/// <summary>
///     One observation of a booking's payment: what was charged, what refund
///     exists if any, and whether that refund is still outstanding.
/// </summary>
public record PaymentStateSnapshot
{
    public required Money Amount { get; init; }

    /// <summary>Null when nothing has been refunded against this payment.</summary>
    public Money? RefundAmount { get; init; }

    /// <summary>
    ///     Where the recorded refund stands. None when nothing has been refunded
    ///     against this payment - including while one is still owed, which is
    ///     <see cref="AwaitingRefund"/>.
    /// </summary>
    public RefundStatus RefundStatus { get; init; }

    /// <summary>
    ///     Whether money was collected and has not been given back.
    /// </summary>
    public bool AwaitingRefund { get; init; }

    /// <summary>
    ///     When the payment succeeded, null on rows predating that column. The
    ///     input a caller needs to ask RefundDecision what an outstanding refund
    ///     will come to, rather than recomputing it by another rule.
    /// </summary>
    public DateTimeOffset? SucceededAt { get; init; }
}

public record TransactionRefundSnapshot
{
    public required Money Amount { get; init; }
    public required Money RefundAmount { get; init; }

    /// <summary>
    ///     Whether that refund is still outstanding.
    ///     <para>
    ///         RefundAmount records what was <em>requested</em>: MarkRefundPending
    ///         sets it, and it stays through Refunded and RefundFailed alike, so
    ///         its presence cannot say a refund settled.
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
