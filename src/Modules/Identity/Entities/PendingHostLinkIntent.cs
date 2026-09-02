namespace Identity.Entities;

/// <summary>
///     A durable marker that BecomeHostHandler has begun cross-module work for
///     a user - written in Identity's own database *before* RegisterHostAsync
///     is called, so every failure mode (ordinary exception or hard process
///     death) leaves something to recover from. The Identity counterpart of
///     PendingBookingIntent; see docs/adr/0017.
///     <para>
///         BecomeHost was the last forward-half cross-module write in the
///         codebase not covered by an intent row or an outbox row in the same
///         transaction as its state change. RegisterHostAsync commits a Host
///         in Hosts' database before Identity writes anything; the
///         failed-update paths compensate through the outbox, but a process
///         death between the two wrote nothing anywhere, and no job could
///         find the orphan.
///     </para>
///     <para>
///         Persistence-layer construct, not a Domain aggregate - same
///         reasoning as PendingBookingIntent: no business methods, no
///         soft-delete or audit trail to carry, and resolving it means DELETE
///         rather than a status flip.
///     </para>
///     <para>
///         <b>Id is the pre-generated hostId</b>, not a fresh value. That is
///         what lets ReconcileOrphanedHostLinkIntentsJob delete the orphaned
///         Host with no cross-module lookup, and - because RegisterHostAsync
///         now takes the id rather than minting one - what makes a retried
///         BecomeHost re-register the same Host instead of creating another.
///     </para>
/// </summary>
public sealed class PendingHostLinkIntent
{
    /// <summary>
    ///     How long an intent may sit unresolved before the reconcile job
    ///     treats it as abandoned. Same value and same reasoning as
    ///     PendingBookingIntent.ReconcileGrace - comfortably beyond worst-case
    ///     request duration under EnableRetryOnFailure's retries.
    ///     <para>
    ///         Correctness does not depend on it. The success path deletes
    ///         this row in the same SaveChanges that sets ApplicationUser.HostId,
    ///         so a linked user can never have a surviving intent for the job
    ///         to act on. The grace period only trades how long an orphaned
    ///         Host lingers against how often the job races a slow-but-healthy
    ///         request.
    ///     </para>
    /// </summary>
    public static readonly TimeSpan ReconcileGrace = TimeSpan.FromMinutes(10);

    /// <summary>The Host id this attempt registered, or is about to.</summary>
    public Guid Id { get; set; }

    /// <summary>
    ///     Unique, so one user can never accumulate intents. A retry reuses
    ///     the existing row's Id rather than allocating a second host - which
    ///     is what turns "retry three times, get three orphaned Hosts" into
    ///     three no-op registrations of the same one.
    /// </summary>
    public Guid UserId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
