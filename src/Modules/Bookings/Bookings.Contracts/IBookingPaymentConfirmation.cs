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
    ///     Marks the booking as confirmed (BookingStatus 'Pending' ->
    ///     'Confirmed') and returns true - unless the booking was already
    ///     Cancelled (e.g. the customer cancelled while this same payment
    ///     was still in flight at the gateway), in which case it's left
    ///     untouched and this returns false instead of throwing. A webhook
    ///     reporting a payment succeeded is a fact about something that
    ///     already happened externally, not a request that can just be
    ///     rejected - MarkTransactionSucceededHandler uses a false result
    ///     as its signal to start a refund instead of treating this as the
    ///     ordinary case. Idempotent if already Confirmed. Throws
    ///     NotFoundException if the booking doesn't exist.
    /// </summary>
    Task<bool> ConfirmPaymentAsync(Guid bookingId, CancellationToken cancellationToken);
}
