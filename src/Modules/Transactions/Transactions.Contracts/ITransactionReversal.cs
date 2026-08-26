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
    ///     can surface it back to whoever's cancelling.
    /// </summary>
    Task<decimal?> ReverseTransactionAsync(Guid bookingId, decimal refundAmount, CancellationToken cancellationToken);
}
