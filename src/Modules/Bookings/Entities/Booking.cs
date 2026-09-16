using Ardalis.GuardClauses;
using Bookings.Contracts;
using SeedWork.Abstractions;
using SeedWork.ValueObjects;
namespace Bookings.Entities;

public sealed class Booking : Entity
{
    // EF materialization only: its constructor binding cannot bind a complex property (docs/adr/0015).
    // The string defaults satisfy nullability and are overwritten immediately.
    private Booking()
    {
        GuestName = string.Empty;
        GuestEmail = string.Empty;
        TimeZoneId = string.Empty;
    }

    private Booking(
        Guid id,
        Guid unitId,
        Guid holdId,
        Guid? customerId,
        string guestName,
        string guestEmail,
        string? guestPhone,
        DateOnly checkIn,
        DateOnly checkOut,
        int guestCount,
        Money totalPrice,
        Money subtotal,
        BookingStatus bookingStatus,
        CancellationPolicy cancellationPolicy,
        string timeZoneId,
        DateTimeOffset? paymentDueAt)
    {
        Id = id;
        UnitId = unitId;
        HoldId = holdId;
        CustomerId = customerId;
        GuestName = guestName;
        GuestEmail = guestEmail;
        GuestPhone = guestPhone;
        CheckIn = checkIn;
        CheckOut = checkOut;
        GuestCount = guestCount;
        TotalPrice = totalPrice;
        _subtotal = subtotal.Amount;
        BookingStatus = bookingStatus;
        CancellationPolicy = cancellationPolicy;
        TimeZoneId = timeZoneId;
        PaymentDueAt = paymentDueAt;
    }

    // Cross-module references, so plain Guids rather than FKs (docs/adr/0004).
    public Guid UnitId { get; private set; }
    public Guid HoldId { get; private set; }

    // Null for guest checkout. The contact details are a snapshot, so they do not shift with the account.
    public Guid? CustomerId { get; private set; }
    public string GuestName { get; private set; }
    public string GuestEmail { get; private set; }
    public string? GuestPhone { get; private set; }

    public DateOnly CheckIn { get; private set; }
    public DateOnly CheckOut { get; private set; }
    public int GuestCount { get; private set; }

    // A booking carries one currency, and it lives here.
    public Money TotalPrice { get; private set; }

    // One decimal column, exposed as Money paired with TotalPrice's currency. Snapshotted from the
    // hold, never reconstructed from total plus discount (docs/adr/0015).
    private decimal _subtotal;

    public Money Subtotal => Money.Of(_subtotal, TotalPrice.Currency);

    // Not Status: Entity.Status is the soft-delete state.
    public BookingStatus BookingStatus { get; private set; }

    /// <summary>
    ///     When this booking's claim on its unit lapses if nobody pays; ExpireUnpaidBookingsJob cancels
    ///     the booking and releases the hold together once it passes (docs/adr/0020). Null once the
    ///     booking is no longer awaiting payment.
    /// </summary>
    public DateTimeOffset? PaymentDueAt { get; private set; }

    /// <summary>
    ///     When this booking was cancelled; null while it is not. Entity.ModifiedAt cannot serve, since
    ///     every later write overwrites it. The refund decision orders against the obligation's
    ///     cancellation instant, not this one (docs/adr/0027).
    /// </summary>
    public DateTimeOffset? CancelledAt { get; private set; }

    // Snapshotted at confirm: a host tightening their policy cannot worsen a confirmed guest's terms.
    // Null only on rows written without one, where CancelBookingHandler falls back to CreateDefault().
    public CancellationPolicy? CancellationPolicy { get; private set; }

    // Snapshotted for the same reason, and non-nullable: a null zone would fall back to UTC, the error
    // ADR-0018 exists to remove.
    public string TimeZoneId { get; private set; }

    // The id is the caller's: a redeemed promo code needs it before the booking is saved, and a retry
    // must be able to find the row it already wrote (docs/adr/0025).
    public static Booking Create(
        Guid id,
        Guid unitId,
        Guid holdId,
        Guid? customerId,
        string guestName,
        string guestEmail,
        string? guestPhone,
        DateOnly checkIn,
        DateOnly checkOut,
        int guestCount,
        Money totalPrice,
        Money subtotal,
        CancellationPolicy cancellationPolicy,
        string timeZoneId,
        DateTimeOffset paymentDueAt)
    {
        Guard.Against.Default(id);
        Guard.Against.Default(unitId);
        Guard.Against.Default(holdId);
        Guard.Against.NullOrWhiteSpace(guestName);
        Guard.Against.NullOrWhiteSpace(guestEmail);
        Guard.Against.InvalidFormat(guestEmail, nameof(guestEmail),
            @"^[^@\s]+@[^@\s]+\.[^@\s]+$", "Guest email is not a valid email address.");
        Guard.Against.InvalidInput(checkOut, nameof(checkOut),
            c => c > checkIn, "Check-out must be after check-in.");
        Guard.Against.NegativeOrZero(guestCount);

        // Zero is refused, not merely negative: Transaction.Create rejects a zero amount, so a free
        // booking could never be paid for. ConfirmBookingHandler rejects the same case with a message.
        Guard.Against.NegativeOrZero(totalPrice.Amount);
        Guard.Against.Negative(subtotal.Amount);
        Guard.Against.Null(cancellationPolicy);
        Guard.Against.NullOrWhiteSpace(timeZoneId);

        // Required: a booking with no deadline is an unbounded claim on a unit's calendar.
        Guard.Against.Default(paymentDueAt);

        return new Booking(
            id, unitId, holdId, customerId, guestName, guestEmail, guestPhone,
            checkIn, checkOut, guestCount, totalPrice, subtotal, BookingStatus.Pending, cancellationPolicy, timeZoneId,
            paymentDueAt);
    }

    /// <summary>
    ///     Whether a guest may cancel this booking themselves, as of the given property-local date.
    ///     Reaching a booking and cancelling it are different questions: a management link stays usable
    ///     after checkout so a finished stay can still be viewed. Cancellation ends at check-in, because
    ///     cutting a stay short is a different transaction with different money attached.
    /// </summary>
    public bool CanBeCancelledOn(DateOnly today)
    {
        return BookingStatus != BookingStatus.Cancelled && today < CheckIn;
    }

    // Idempotent, and deliberately without a date guard: CanBeCancelledOn is the self-service policy
    // over this transition, and ExpireUnpaidBookingsJob cancels on the system's behalf.
    public void Cancel(DateTimeOffset cancelledAt)
    {
        if (BookingStatus == BookingStatus.Cancelled)
        {
            return;
        }

        BookingStatus = BookingStatus.Cancelled;
        CancelledAt = cancelledAt;
        PaymentDueAt = null;
    }

    // Idempotent, but throws from Cancelled: a payment succeeding against a booking cancelled out from
    // under it is a real inconsistency, and the caller answers it with a refund.
    public void Confirm()
    {
        if (BookingStatus == BookingStatus.Confirmed)
        {
            return;
        }

        if (BookingStatus != BookingStatus.Pending)
        {
            throw new BookingNotPayableException(Id);
        }

        BookingStatus = BookingStatus.Confirmed;

        // Cleared, or a paid booking reads as overdue to anything watching the field.
        PaymentDueAt = null;
    }
}
