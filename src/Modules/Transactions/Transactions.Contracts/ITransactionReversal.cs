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
    ///     transaction, moves it to RefundPending - money was actually
    ///     collected, so it needs reversing. A no-op for everything else,
    ///     including a still-Pending transaction: we don't yet know what
    ///     the gateway will do with it, so it's left alone rather than
    ///     guessed at - see IBookingPaymentConfirmation.ConfirmPaymentAsync
    ///     for how a late success against an already-cancelled booking is
    ///     handled instead, on the other side of that eventual outcome.
    /// </summary>
    Task ReverseTransactionAsync(Guid bookingId, CancellationToken cancellationToken);
}
