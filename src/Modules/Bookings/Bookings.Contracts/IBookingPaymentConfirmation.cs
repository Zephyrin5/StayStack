namespace Bookings.Contracts;

/// <summary>
///     Write-side counterpart to IBookingLookup - lets Transactions turn a
///     succeeded transaction into a Confirmed booking without ever seeing
///     the Booking entity or touching BookingsDb directly. Same
///     boundary reasoning as Catalog.Contracts.IHoldConfirmation.
/// </summary>
public interface IBookingPaymentConfirmation
{
    /// <summary>
    ///     Turns a succeeded payment into a sold stay: marks the hold 'booked' and the booking
    ///     Confirmed, and returns true.
    ///     <para>
    ///         False when the payment cannot become a stay - the booking was cancelled while it was in
    ///         flight, or its hold was released or expired. A payment succeeding is a fact, not a
    ///         request that can be refused, so both are reported rather than thrown: the caller's
    ///         answer to either is the same refund, and throwing would retry an outcome that cannot
    ///         improve.
    ///     </para>
    ///     <para>
    ///         Runs only inside the caller's transaction, so both halves commit with the payment;
    ///         throws InvalidOperationException outside one, and NotFoundException for no such booking.
    ///     </para>
    /// </summary>
    Task<bool> ConfirmPaymentAsync(Guid bookingId, CancellationToken cancellationToken);
}
