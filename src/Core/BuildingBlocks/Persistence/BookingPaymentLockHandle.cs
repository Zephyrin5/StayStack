namespace BuildingBlocks.Persistence;

/// <summary>
///     Proof that <see cref="BookingPaymentLock"/> is held for one booking, on the caller's
///     transaction.
///     <para>
///         Only <see cref="BookingPaymentLock"/> can make one, so a path that cancels a booking or
///         claims its row cannot be written without taking the lock first: the parameter is missing
///         and the code does not compile. That is what the protocol test used to watch for, and it
///         watched from a distance - a new cancelling path looked complete on its own.
///     </para>
/// </summary>
public sealed class BookingPaymentLockHandle
{
    internal BookingPaymentLockHandle(Guid bookingId) => BookingId = bookingId;

    /// <summary>The booking the lock was taken for. Checked by everything that accepts a handle.</summary>
    public Guid BookingId { get; }
}
