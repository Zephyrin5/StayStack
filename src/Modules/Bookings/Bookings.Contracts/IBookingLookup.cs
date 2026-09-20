using SeedWork.ValueObjects;
namespace Bookings.Contracts;

/// <summary>
///     Lets Transactions resolve a booking's amount/currency without ever
///     referencing Bookings' own entities or BookingsDb directly -
///     same boundary reasoning as Catalog.Contracts.IUnitLookup.
/// </summary>
public interface IBookingLookup
{
    Task<BookingSummary?> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     The ownership proof CancelBookingHandler uses - a matching customerId, or a matching
    ///     management token - through the same BookingAccessChecker. Null for both "does not exist"
    ///     and "not yours", which must look identical.
    /// </summary>
    Task<BookingAccessResult?> VerifyBookingAccessAsync(
        Guid bookingId, Guid? customerId, CancellationToken cancellationToken);

    /// <summary>
    ///     Confirmed bookings for this customer whose checkout falls in
    ///     <paramref name="checkOutFrom"/>..<paramref name="checkOutTo"/> inclusive, which Reviews
    ///     narrows to not-yet-reviewed.
    ///     <para>
    ///         The range is a parameter because Bookings has no notion of a review window, and
    ///         required because the endpoint has no pagination - bounding the query bounds the
    ///         response. A caller filtering on a property-local date widens it by a day either side: a
    ///         local date is within one day of the UTC date in every zone, so the range is a safe
    ///         superset and the exact per-zone check belongs where each booking's zone is known.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<BookingAccessResult>> GetConfirmedBookingsForCustomerAsync(
        Guid customerId, DateOnly checkOutFrom, DateOnly checkOutTo, CancellationToken cancellationToken);

    /// <summary>
    ///     A raw lookup with no ownership check: a host reviewing a guest is authorized by owning the
    ///     booking's unit, not by the customerId or token match the method above tests. Null when the
    ///     booking does not exist.
    /// </summary>
    Task<BookingAccessResult?> GetBookingDetailsAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     The refund this booking's cancellation committed to owing, if it was
    ///     cancelled. Null when it was not.
    ///     <para>
    ///         Written in the cancellation's own transaction, so a reader either sees it with a true
    ///         CancelledAt or sees nothing - never a null standing in for "committed but not visible
    ///         yet", which is what made reading CancelledAt off the booking unsafe.
    ///     </para>
    /// </summary>
    Task<RefundObligationSnapshot?> GetRefundObligationAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     Marks the obligation settled, with how it ended: a refund recorded against the transaction,
    ///     or nothing owed.
    ///     <para>
    ///         The second of two commits, and why the first is safe to repeat: a crash in between
    ///         leaves this unset, the sweep tries again, and MarkRefundPending's first-wins guard
    ///         makes the repeat a no-op rather than a second refund.
    ///     </para>
    /// </summary>
    Task MarkRefundObligationResolvedAsync(
        Guid bookingId, DateTimeOffset resolvedAt, RefundObligationOutcome outcome, CancellationToken cancellationToken);
}

public record RefundObligationSnapshot
{
    public required Guid BookingId { get; init; }
    public required DateTimeOffset CancelledAt { get; init; }

    /// <summary>What the cancellation policy resolved to at cancellation time.</summary>
    public required Money PolicyRefundAmount { get; init; }

    /// <summary>Whether a refund has already been decided for this obligation.</summary>
    public required bool IsResolved { get; init; }

    /// <summary>
    ///     Whether the guest cancelled, or the unpaid-checkout sweep did. The
    ///     resolver records it on the transaction so the reason a refund
    ///     happened survives past the row that caused it.
    /// </summary>
    public required BookingCancellationCause Cause { get; init; }
}

public record BookingSummary
{
    public Guid Id { get; init; }
    public Money TotalPrice { get; init; }

    // True only while Pending - the one state a transaction can actually
    // be initiated from. Exposed as a bool rather than the real
    // BookingStatus enum since that type lives in Bookings.Entities, not
    // this dependency-free contract, and callers only ever need this one
    // fact about it.
    public bool IsPending { get; init; }
}

public record BookingAccessResult
{
    public Guid BookingId { get; init; }
    public Guid UnitId { get; init; }
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }

    // Same bool-not-enum reasoning as BookingSummary.IsPending - Reviews
    // only ever needs this one fact about BookingStatus.
    public bool IsConfirmed { get; init; }

    /// <summary>
    ///     When this booking was cancelled, null while it is not. Read by the
    ///     refund paths in Transactions to order a cancellation against a
    ///     payment - a timestamp rather than a bool, because "was it cancelled"
    ///     is not the question; "was it cancelled before the money moved" is.
    /// </summary>
    public DateTimeOffset? CancelledAt { get; init; }

    // The two facts a payment needs, on the result that proves access, so
    // InitiateTransactionHandler reads them only after ownership is verified.
    public Money TotalPrice { get; init; }

    // True only while Pending - the one state a transaction can be initiated
    // from. Same bool-not-enum reasoning as IsConfirmed above.
    public bool IsPending { get; init; }
    public required string GuestEmail { get; init; }
    public Guid? CustomerId { get; init; }

    // The booking's own snapshotted zone, so callers resolve "today" per
    // booking rather than once per request - what makes a list spanning
    // several properties correct (docs/adr/0018).
    public required string TimeZoneId { get; init; }
}
