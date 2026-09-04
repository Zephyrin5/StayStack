namespace Bookings.Contracts;

/// <summary>
///     Write-side counterpart to IBookingLookup - lets Transactions turn a
///     succeeded transaction into a Confirmed booking without ever seeing
///     the Booking entity or touching AppBookingsDbContext directly. Same
///     boundary reasoning as Catalog.Contracts.IHoldConfirmation.
/// </summary>
public interface IBookingPaymentConfirmation
{
    /// <summary>
    ///     Turns a succeeded payment into a sold stay: marks the hold
    ///     'booked' and the booking Confirmed, and returns true.
    ///     <para>
    ///         Returns false when the payment cannot be turned into a stay at
    ///         all - the booking was cancelled while this payment was still
    ///         in flight at the gateway, or its hold was released or expired
    ///         before the payment resolved and the range is no longer this
    ///         booking's to sell. A webhook reporting a payment succeeded is
    ///         a fact about something that already happened externally, not a
    ///         request that can be rejected, so both cases are reported
    ///         rather than thrown: the caller's answer to either is the same
    ///         refund. Throwing would retry an outcome that will never
    ///         improve.
    ///     </para>
    ///     <para>
    ///         Idempotent. A retried outbox message re-runs both halves; the
    ///         hold transition accepts an already-'booked' hold and the
    ///         booking transition is a no-op once Confirmed.
    ///     </para>
    ///     <para>
    ///         Throws NotFoundException if the booking doesn't exist.
    ///     </para>
    /// </summary>
    Task<bool> ConfirmPaymentAsync(Guid bookingId, CancellationToken cancellationToken);
}
