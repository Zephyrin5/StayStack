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
    ///     'Confirmed'). Throws NotFoundException if the booking doesn't
    ///     exist. Idempotent if already Confirmed; throws if the booking
    ///     was Cancelled - see Booking.Confirm() for the actual invariant.
    /// </summary>
    Task ConfirmPaymentAsync(Guid bookingId, CancellationToken cancellationToken);
}
