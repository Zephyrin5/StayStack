using SeedWork.ValueObjects;
namespace Bookings.Contracts;

/// <summary>
///     The hold's write side. This used to live in Availability.Contracts
///     and exist so Bookings could move a hold without seeing the table;
///     both now live in this module, so it is internal and the seam it
///     described is gone.
///     <para>
///         Kept as an interface rather than folded into the implementation:
///         the unit tests mock it, which is reason enough on its own.
///     </para>
///     <para>
///         Still public, though it is no longer a cross-module contract.
///         Making it internal would force ConfirmBookingHandler, three jobs
///         and BookingsOutboxDispatcher internal with it - types that
///         Mediator, TickerQ and DI discover - which is a lot of churn and
///         some discovery risk to express something the file's location
///         already says. What mattered was deleting the contracts project
///         that carried it across a module boundary.
///     </para>
/// </summary>
public interface IHoldConfirmation
{
    /// <summary>
    ///     Claims the hold for a checkout in progress ('held' ->
    ///     'pending_payment'). Throws NotFoundException if the hold doesn't
    ///     exist, has already been consumed, or has expired - Bookings never
    ///     sees a stale/expired hold succeed silently.
    ///     <para>
    ///         Not 'booked': submitting a checkout form is not paying for
    ///         anything. This used to write 'booked' directly, which made
    ///         every submitted form permanent inventory - nothing reclaimed
    ///         such a row, and it blocked its range through the exclusion
    ///         constraint indefinitely. The caller owns the deadline (see
    ///         Booking.PaymentDueAt) and releases the hold when it passes.
    ///     </para>
    /// </summary>
    Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken);

    /// <summary>
    ///     Completes the lifecycle on payment ('pending_payment' ->
    ///     'booked'), the one transition that turns a reservation into sold
    ///     inventory nothing reclaims on a timer. Returns false when no row
    ///     moved, which means the hold was already released or expired out
    ///     from under a late-arriving payment - the caller has taken money
    ///     for a range it no longer holds and must compensate rather than
    ///     treat this as success.
    /// </summary>
    /// <summary>
    ///     The snapshot <see cref="ConfirmHoldAsync"/> would have returned, for
    ///     a hold already in 'pending_payment' - without transitioning
    ///     anything. Null when the hold is in any other state.
    ///     <para>
    ///         Exists for exactly one caller: ConfirmBookingHandler's
    ///         execution-strategy retry path. Now that the hold transition and
    ///         the PendingBookingIntent insert commit in one transaction,
    ///         finding our own intent already present proves the hold moved
    ///         with it, and re-calling ConfirmHoldAsync would fail its
    ///         status = 'held' guard. This is how that attempt reads back what
    ///         it already did.
    ///     </para>
    /// </summary>
    Task<ConfirmedHold?> GetConfirmedHoldAsync(Guid holdId, CancellationToken cancellationToken);

    Task<bool> MarkHoldPaidAsync(Guid holdId, CancellationToken cancellationToken);

    /// <summary>
    ///     Returns a claimed or sold hold ('pending_payment' or 'booked')
    ///     back to 'held' with hold_expires_at reset to now, so the ordinary
    ///     expiry sweep reclaims it immediately rather than after whatever
    ///     was left on its original 15-minute window. Used by
    ///     ConfirmBookingHandler's compensating paths, CancelBookingHandler,
    ///     ReconcileOrphanedBookingIntentsJob, and the unpaid-booking expiry
    ///     job. Best-effort/idempotent: a no-op if the hold is in neither
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

    // The pre-discount total, snapshotted on the hold itself rather than
    // left for ConfirmBookingHandler to reconstruct via TotalPrice +
    // LengthOfStayDiscountAmount - that reconstruction is exactly the
    // rounding bug docs/adr/0015 exists to close, since each side was
    // independently rounded. Shares TotalPrice.Currency.
    public Money Subtotal { get; init; }

    // Same snapshot as TotalPrice/Subtotal, carried through so
    // ConfirmBookingHandler can undo just the length-of-stay portion when a
    // redeemed promo code is exclusive of it rather than stacking - see
    // Catalog.Contracts.IUnitLookup.StayPricingResult for why this needs to
    // travel separately from the final total.
    public Money? LengthOfStayDiscountAmount { get; init; }
}
