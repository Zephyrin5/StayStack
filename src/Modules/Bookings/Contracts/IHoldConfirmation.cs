using SeedWork.ValueObjects;
namespace Bookings.Contracts;

/// <summary>
///     The hold's write side.
///     <para>
///         An interface because the unit tests mock it. Public although only
///         this module uses it: internal would force ConfirmBookingHandler, three
///         jobs and BookingsOutboxDispatcher internal with it, types that
///         Mediator, TickerQ and DI discover.
///     </para>
/// </summary>
public interface IHoldConfirmation
{
    /// <summary>
    ///     Claims the hold for a checkout in progress ('held' ->
    ///     'pending_payment'). Throws NotFoundException if the hold doesn't
    ///     exist, has already been consumed, or has expired.
    ///     <para>
    ///         Not 'booked': submitting a checkout form is not paying. The caller
    ///         owns the deadline (Booking.PaymentDueAt) and releases the hold
    ///         when it passes.
    ///     </para>
    /// </summary>
    Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken);

    /// <summary>
    ///     Completes the lifecycle on payment ('pending_payment' ->
    ///     'booked'), the one transition that turns a reservation into sold
    ///     inventory nothing reclaims on a timer. Returns false when no row
    ///     moved: the hold was released or expired under a late-arriving
    ///     payment, and the caller has taken money for a range it no longer
    ///     holds and must compensate.
    /// </summary>
    Task<bool> MarkHoldPaidAsync(Guid holdId, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns a claimed or sold hold ('pending_payment' or 'booked')
    ///     back to 'held' with hold_expires_at reset to now, so the ordinary
    ///     expiry sweep reclaims it immediately rather than after whatever
    ///     was left on its original 15-minute window. Used by
    ///     CancelBookingHandler, ReconcileOrphanedBookingIntentsJob, and the
    ///     unpaid-booking expiry job. Best-effort/idempotent: a no-op if the hold is in neither
    ///     state (already released, or never existed).
    /// </summary>
    Task ReleaseHoldAsync(Guid holdId, CancellationToken cancellationToken);
}

public record ConfirmedHold
{
    public Guid UnitId { get; init; }
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public int GuestCount { get; init; }
    public Money TotalPrice { get; init; }

    // The pre-discount total, snapshotted on the hold rather than reconstructed
    // from TotalPrice + LengthOfStayDiscountAmount, which were rounded
    // independently (docs/adr/0015). Shares TotalPrice.Currency.
    public Money Subtotal { get; init; }

    // Carried separately so ConfirmBookingHandler can undo just the
    // length-of-stay portion when a promo code replaces it - see
    // Catalog.Contracts.IUnitLookup.StayPricingResult.
    public Money? LengthOfStayDiscountAmount { get; init; }
}
