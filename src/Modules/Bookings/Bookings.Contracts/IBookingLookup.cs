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
    ///     Lets Reviews authorize a review submission without ever
    ///     referencing Booking or BookingsDb directly - same
    ///     ownership proof CancelBookingHandler itself uses (a matching
    ///     customerId, or a matching guest-checkout management token), via
    ///     the same internal BookingAccessChecker both go through. Null if
    ///     the booking doesn't exist or the caller doesn't own it - doesn't
    ///     distinguish the two, same "doesn't exist and isn't yours must
    ///     look identical" reasoning as everywhere else this pattern is used.
    /// </summary>
    Task<BookingAccessResult?> VerifyBookingAccessAsync(
        Guid bookingId, Guid? customerId, CancellationToken cancellationToken);

    /// <summary>
    ///     Confirmed bookings for this customer whose checkout falls in
    ///     <paramref name="checkOutFrom"/>..<paramref name="checkOutTo"/>
    ///     inclusive - what ListMyReviewableBookingsHandler (Reviews) narrows
    ///     to not-yet-reviewed, since Reviews has no notion of
    ///     Booking/CustomerId itself.
    ///     <para>
    ///         The range is a parameter because Bookings has no notion of a review
    ///         window. It is required because the endpoint has no pagination:
    ///         bounding the query to the window bounds the response.
    ///     </para>
    ///     <para>
    ///         Callers filtering on a property-local date should widen by a
    ///         day either side: a local date sits within one day of the UTC
    ///         date in every timezone, so the range is a safe superset and the
    ///         exact per-zone check belongs at the call site, which is the
    ///         only place that knows each booking's zone.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<BookingAccessResult>> GetConfirmedBookingsForCustomerAsync(
        Guid customerId, DateOnly checkOutFrom, DateOnly checkOutTo, CancellationToken cancellationToken);

    /// <summary>
    ///     A raw lookup, no ownership check - what CreateGuestReviewHandler
    ///     (Reviews) uses, since a host reviewing a guest is authorized by
    ///     owning the booking's unit (via Catalog.Contracts.IUnitLookup),
    ///     not by a customerId/managementToken match the way
    ///     VerifyBookingAccessAsync's two paths are. Null if the booking
    ///     doesn't exist.
    /// </summary>
    Task<BookingAccessResult?> GetBookingDetailsAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     The refund this booking's cancellation committed to owing, if it was
    ///     cancelled. Null when it was not.
    ///     <para>
    ///         Written in the same transaction as the cancellation, so a reader
    ///         either sees it with a true CancelledAt or sees nothing - never a
    ///         null timestamp standing in for "committed but not yet visible",
    ///         which is what made reading CancelledAt off the booking unsafe.
    ///     </para>
    /// </summary>
    Task<RefundObligationSnapshot?> GetRefundObligationAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     Marks the obligation settled, once a refund has been recorded
    ///     against the transaction.
    ///     <para>
    ///         The second of two commits, and the reason the first is safe to
    ///         repeat: a crash in between leaves this unset, the backstop job
    ///         tries again, and MarkRefundPending's own first-wins guard makes
    ///         the repeated write a no-op rather than a second refund.
    ///     </para>
    /// </summary>
    Task MarkRefundObligationResolvedAsync(
        Guid bookingId, DateTimeOffset resolvedAt, CancellationToken cancellationToken);
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
