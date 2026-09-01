namespace Bookings.Entities;

/// <summary>
///     A durable marker that ConfirmBookingHandler has begun cross-module work
///     for a hold - written in Bookings' own database *before* the first
///     cross-module call, so every failure mode (ordinary exception or hard
///     process death) leaves something to recover from. See docs/adr/0017.
///     <para>
///         Persistence-layer construct, not a Domain aggregate - same
///         reasoning as BookingManagementToken: no business methods, no
///         soft-delete or audit trail to carry.
///     </para>
///     <para>
///         The row exists only while the work is in flight - resolving it
///         means DELETE, not a status flip. It carries nothing Booking
///         doesn't already record, so a resolved row would be pure
///         accumulation (one per confirm attempt, forever), and
///         ReconcileOrphanedBookingIntentsJob's own counter already answers
///         "how often does crash recovery fire". Deleting also collapses the
///         schema: no status column, so the uniqueness guard below is a plain
///         index rather than a partial one.
///     </para>
///     <para>
///         Id is deliberately the pre-generated bookingId, not a fresh value -
///         it's what RedeemAsync is already keyed by, which is what lets the
///         reconcile job reverse a redemption without any cross-module lookup,
///         and what lets a failed save ask the database whether the Booking
///         actually committed.
///     </para>
/// </summary>
public sealed class PendingBookingIntent
{
    /// <summary>
    ///     How long an intent may sit unresolved before
    ///     ReconcileOrphanedBookingIntentsJob treats it as abandoned. Lives on
    ///     the entity rather than the job because ConfirmBookingHandler reads
    ///     it too (to tell a genuinely concurrent confirmation apart from a
    ///     crashed one when reporting a conflict) - keeping one constant keeps
    ///     the two in sync by construction.
    ///     <para>
    ///         Correctness does not depend on this value: the success path's
    ///         tracked delete is what makes a reconciled booking impossible to
    ///         write (docs/adr/0017). It only trades how long a crashed
    ///         confirm holds inventory against how often the job needlessly
    ///         races a slow-but-healthy request. Ten minutes comfortably
    ///         exceeds worst-case request duration under
    ///         EnableRetryOnFailure's 6 retries. Shortening it should follow
    ///         from an enforced request timeout, so the bound is real rather
    ///         than assumed.
    ///     </para>
    /// </summary>
    public static readonly TimeSpan ReconcileGrace = TimeSpan.FromMinutes(10);

    public Guid Id { get; set; }
    public Guid HoldId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
