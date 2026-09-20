using SeedWork.ValueObjects;
namespace Bookings.Contracts;

/// <summary>
///     The payment behind a booking, and the refund it is owed.
///     <para>
///         Declared by the module that needs it rather than the one that implements it: Transactions
///         references Bookings.Contracts, so the reverse reference would make the two modules mutually
///         dependent (docs/adr/0004). Transactions implements this.
///     </para>
/// </summary>
public interface IPaymentReversal
{
    /// <summary>
    ///     Everything a caller needs to describe this booking's payment, read
    ///     once.
    ///     <para>
    ///         One read because the state moves: a resolver can take a payment
    ///         Succeeded -> RefundPending between two reads, and a refund
    ///         lookup followed by a succeeded-amount lookup would see neither.
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
    ///         Safe to call repeatedly: it is a no-op unless there is a Succeeded
    ///         transaction and an unresolved obligation. Runs only inside the caller's transaction;
    ///         throws InvalidOperationException outside one.
    ///     </para>
    ///     <para>
    ///         Returns the amount recorded, or null when there was nothing to
    ///         do.
    ///     </para>
    /// </summary>
    Task<decimal?> ResolveRefundAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     The bookings whose payments have not finished having a say: any transaction that is not
    ///     Failed. Unexecuted, so the sweep composes it into its own candidate query.
    ///     <para>
    ///         Wider than "a payment could still succeed", which is Pending alone, because the sweep is
    ///         also the backstop for the two-commit resolution: a refund recorded against a Succeeded
    ///         payment whose obligation marker did not commit leaves a row only this sweep will finish,
    ///         and Pending alone would hide it forever (docs/adr/0027).
    ///     </para>
    /// </summary>
    IQueryable<Guid> BookingIdsWithAnUnsettledPayment();
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
    ///     When the payment succeeded, and the input RefundDecision needs. Null only when nothing
    ///     succeeded, which is also when this snapshot is null.
    /// </summary>
    public DateTimeOffset? SucceededAt { get; init; }
}
