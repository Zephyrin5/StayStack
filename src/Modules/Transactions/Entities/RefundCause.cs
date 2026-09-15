namespace Transactions.Entities;

/// <summary>
///     Why a refund was started, recorded with the amount so the reason is
///     not inferred from the figure.
/// </summary>
public enum RefundCause
{
    /// <summary>
    ///     The guest cancelled a stay they had already paid for, so the
    ///     cancellation policy decides the amount.
    /// </summary>
    GuestCancellation = 0,

    /// <summary>
    ///     The payment resolved against a booking that no longer existed, or a
    ///     hold that had been released, so it bought nothing and the whole
    ///     amount goes back.
    /// </summary>
    PaymentUnusable = 1,

    /// <summary>
    ///     The unpaid-checkout sweep reclaimed the booking, and a payment
    ///     landed against it anyway. Distinct from GuestCancellation because
    ///     nobody asked for it - the platform took the inventory back - so a
    ///     cancellation fee would be charging a guest for the platform's own
    ///     deadline.
    /// </summary>
    BookingExpired = 2
}
