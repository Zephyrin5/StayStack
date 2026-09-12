namespace Bookings.Contracts;

/// <summary>
///     Why a booking was cancelled - a different question from why a refund was
///     issued, which Transactions.Entities.RefundCause answers and the refund
///     resolver maps this onto.
///     <para>
///         In Contracts rather than beside RefundObligation itself, because it
///         travels on RefundObligationSnapshot across the module boundary and
///         Transactions cannot see Bookings' entities.
///     </para>
/// </summary>
public enum BookingCancellationCause
{
    GuestCancellation = 0,

    /// <summary>
    ///     ExpireUnpaidBookingsJob reclaimed the inventory behind a checkout
    ///     nobody paid for in time. Ordinarily there is no payment and so
    ///     nothing to refund - but a payment committing in the gap between that
    ///     job's guard and its cancellation produces exactly the case the
    ///     obligation exists to catch.
    /// </summary>
    Expiry = 1
}
