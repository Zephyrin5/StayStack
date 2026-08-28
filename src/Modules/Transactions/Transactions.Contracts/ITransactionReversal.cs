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
    /// <summary>
    ///     Best-effort, never throws: if the booking has a Succeeded
    ///     transaction, moves it to RefundPending with the given amount -
    ///     money was actually collected, so it needs reversing. A no-op for
    ///     everything else, including a still-Pending transaction: we don't
    ///     yet know what the gateway will do with it, so it's left alone
    ///     rather than guessed at - see
    ///     IBookingPaymentConfirmation.ConfirmPaymentAsync for how a late
    ///     success against an already-cancelled booking is handled instead,
    ///     on the other side of that eventual outcome. refundAmount is
    ///     whatever the caller's own cancellation policy resolved to -
    ///     Transactions has no notion of a policy itself, it just records
    ///     the number it was given. Returns the amount actually reversed,
    ///     or null if there was nothing Succeeded to reverse, so the caller
    ///     can surface it back to whoever's cancelling. Takes Money, not a
    ///     bare decimal, specifically so the currency can be validated
    ///     against the transaction's own before it's trusted (see
    ///     Transaction.MarkRefundPending) - closes a real hole where a
    ///     caller could previously pass a refund computed in the wrong
    ///     currency with nothing to catch it.
    /// </summary>
    Task<decimal?> ReverseTransactionAsync(Guid bookingId, Money refundAmount, CancellationToken cancellationToken);

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
}

public record TransactionRefundSnapshot
{
    public required decimal Amount { get; init; }
    public required decimal RefundAmount { get; init; }
}
