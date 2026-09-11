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
    ///     ReconcileOrphanedBookingIntentsJob treats it as abandoned.
    ///     <para>
    ///         One reader now, not two. ConfirmBookingHandler used to date
    ///         another request's intent against this window to choose between
    ///         "already in progress" and "being cleaned up" - a guess at which
    ///         of two situations it was looking at, made necessary only
    ///         because the intent's unique index was arbitrating races for the
    ///         hold. The conditional UPDATE in ExecuteConfirmAsync does that
    ///         now, so the handler never reads another request's intent and
    ///         this value is the job's alone.
    ///     </para>
    ///     <para>
    ///         Correctness does not depend on this value: the success path's
    ///         tracked delete is what makes a reconciled booking impossible to
    ///         write (docs/adr/0017). It only trades how long a crashed
    ///         confirm holds inventory against how often the job needlessly
    ///         races a slow-but-healthy request - which is now the only thing
    ///         it trades, rather than also setting the wording of a
    ///         user-facing conflict. Ten minutes comfortably
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
