namespace Availability.Contracts;

/// <summary>
///     Lets Bookings find its own orphaned-hold reconciliation candidates
///     without ever referencing Availability's own entities,
///     AppAvailabilityDbContext, or the unit_availability_holds table
///     directly - same boundary reasoning as IHoldConfirmation. Deliberately
///     read-only/separate from IHoldConfirmation: this answers "which holds
///     look orphaned", the actual release still goes through
///     IHoldConfirmation.ReleaseHoldAsync.
/// </summary>
public interface IHoldLookup
{
    /// <summary>
    ///     Ids of holds that transitioned to 'booked' inside the window
    ///     (<paramref name="earliestBookedAt"/>, <paramref name="cutoff"/>] -
    ///     the backstop ADR-0003 anticipated for a hold left 'booked' with
    ///     no booking ever created behind it (a process crash between
    ///     HoldConfirmation.ConfirmHoldAsync and the Booking insert that
    ///     follows in Bookings). Doesn't know which of these actually lack a
    ///     booking - that's Bookings' own table to check, via
    ///     Bookings.Jobs.ReconcileOrphanedBookedHoldsJob.
    ///
    ///     Bounded on both ends deliberately: a successfully-booked hold
    ///     stays 'booked' forever (that's the whole point - it's the
    ///     confirmed booking's own permanent double-booking guard), so a
    ///     query with only an upper bound would match every booking this
    ///     app has ever completed and grow without limit. The lower bound
    ///     (<paramref name="earliestBookedAt"/>) keeps the candidate set to
    ///     roughly one reconciliation window's worth of real crash orphans,
    ///     not this app's entire history. Also capped at
    ///     <paramref name="maxResults"/> as a hard ceiling.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetBookedHoldIdsOlderThanAsync(
        DateTimeOffset cutoff, DateTimeOffset earliestBookedAt, int maxResults, CancellationToken cancellationToken);
}
