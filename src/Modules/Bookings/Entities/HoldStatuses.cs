namespace Bookings.Entities;

/// <summary>
///     The closed set of values <c>unit_availability_holds.status</c> may take.
///     <para>
///         <c>Held</c> → <c>PendingPayment</c> → <c>Booked</c>, and a release from either of the last
///         two returns the row to <c>Held</c> with its timer reset. Every one blocks its range: the
///         exclusion constraint carries no status predicate.
///     </para>
///     <para>
///         Constants rather than literals because the status is a varchar compared in raw SQL, EF
///         expressions and index filters alike, and a predicate left behind when the set grows fails
///         silently - adding <c>PendingPayment</c> while ReleaseHoldAsync matched only <c>Booked</c>
///         would have made every compensating release a zero-row no-op. The schema's CHECK constraint
///         catches a stale write; nothing catches a stale read.
///     </para>
/// </summary>
internal static class HoldStatuses
{
    /// <summary>
    ///     A live but uncommitted hold, expiring on its own 15-minute clock.
    ///     Swept by ExpiredHoldsSweepJob once past <c>hold_expires_at</c>,
    ///     and the only state HoldAvailabilityHandler's per-client cap
    ///     counts.
    /// </summary>
    public const string Held = "held";

    /// <summary>
    ///     Checkout was submitted and a Booking exists, but nothing has been
    ///     paid. Released by ExpireUnpaidBookingsJob when the booking's
    ///     PaymentDueAt passes - deliberately not by ExpiredHoldsSweepJob,
    ///     which would free the range while leaving the guest holding a
    ///     booking with no inventory behind it.
    /// </summary>
    public const string PendingPayment = "pending_payment";

    /// <summary>
    ///     Paid for. Reached only through the payment-confirmation path, and
    ///     the one state nothing reclaims on a timer.
    /// </summary>
    public const string Booked = "booked";
}
