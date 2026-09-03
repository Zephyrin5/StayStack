namespace Availability.Entities;

/// <summary>
///     The closed set of values <c>unit_availability_holds.status</c> may
///     take, and the lifecycle they form.
///     <para>
///         <c>Held</c> → <c>PendingPayment</c> → <c>Booked</c>, with a
///         release from either of the last two returning the row to
///         <c>Held</c> with its timer reset (so the ordinary expiry sweep
///         reclaims it). Every one of these blocks its range: the exclusion
///         constraint on (unit_id, stay_range) carries no status predicate,
///         so a row in any state is inventory held against a guest.
///     </para>
///     <para>
///         Constants rather than literals because the status is a plain
///         varchar compared in raw SQL, EF expressions and partial-index
///         filters alike - nothing type-checks it, and a predicate left
///         behind when the set grows fails silently rather than loudly. That
///         is not hypothetical: introducing <c>PendingPayment</c> while
///         <c>ReleaseHoldAsync</c> still matched only <c>Booked</c> would
///         have turned every compensating release into a zero-row no-op.
///         The migration also carries a CHECK constraint over this set, which
///         catches a stale <em>write</em> even though nothing can catch a
///         stale read predicate.
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
    ///     paid. Released by Bookings' own expiry job when the booking's
    ///     PaymentDueAt passes - deliberately not by Availability's sweep,
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
