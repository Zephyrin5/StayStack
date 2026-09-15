namespace BuildingBlocks.Time;

/// <summary>
///     Resolves an IANA timezone id, and the local business date within it.
///     <para>
///         Business dates in this app are <b>property-local</b>, never UTC:
///         "is this check-in still bookable" and "how many days before
///         check-in is this cancellation" are questions about the hotel's
///         calendar, not the server's or the browser's. See docs/adr/0018.
///     </para>
///     <para>
    ///     <para>
    ///         <b>At read time an unusable timezone is an error, never a guess.</b>
    ///         Nothing here falls back to UTC: under a UTC+3 market that skew is
    ///         permissive and loses money, so a wrong answer is worse than no
    ///         answer. The one exception is the migration that backfilled
    ///         existing rows, a data decision rather than a runtime one.
    ///     </para>
/// </summary>
public static class PropertyTimeZone
{
    /// <summary>
    ///     True if the id resolves on this machine. TryFindSystemTimeZoneById
    ///     rather than FindSystemTimeZoneById, so a validator or guard gets a
    ///     boolean instead of a TimeZoneNotFoundException, which would surface
    ///     as a 500.
    /// </summary>
    public static bool IsValid(string? timeZoneId) =>
        !string.IsNullOrWhiteSpace(timeZoneId)
        && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _);

    /// <summary>
    ///     The current date at the given timezone. Converting an instant to a
    ///     local date is always unambiguous - unlike the reverse direction,
    ///     DST never makes this ill-defined (a wall-clock time can occur twice
    ///     or never; an instant maps to exactly one local date).
    /// </summary>
    /// <exception cref="InvalidOperationException">
    ///     The id is missing or does not resolve. Both are effectively
    ///     unreachable given a required, write-validated column - a null is a
    ///     programming error, and an id that validated at write and later
    ///     vanished from tzdata is an ops error of the same family. Failing
    ///     loudly is the point; see the class remarks.
    /// </exception>
    public static DateOnly Today(TimeProvider timeProvider, string timeZoneId) =>
        ToLocalDate(timeProvider.GetUtcNow(), timeZoneId);

    /// <summary>
    ///     The local date a given instant fell on at that timezone. Used where
    ///     the anchor is a recorded moment rather than now, or where one clock
    ///     reading is shared across many bookings.
    ///     <para>
    ///         Resolves per call rather than caching: TryFindSystemTimeZoneById
    ///         hits the BCL's own cache, about 0.12-0.14 microseconds per call
    ///         including the conversion. A per-request memo would save about
    ///         0.13 milliseconds across 2000 bookings, less than any of the
    ///         database round trips beside the one caller that loops
    ///         (ListMyReviewableBookingsHandler). Worth re-measuring only for a
    ///         hot path with no database work beside it.
    ///     </para>
    /// </summary>
    /// <exception cref="InvalidOperationException">See <see cref="Today" />.</exception>
    public static DateOnly ToLocalDate(DateTimeOffset instant, string timeZoneId)
    {
        if (!TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out TimeZoneInfo? timeZone))
        {
            throw new InvalidOperationException(
                $"Time zone '{timeZoneId}' could not be resolved, so no business date can be computed for it. " +
                "Falling back to UTC would silently shift booking and refund boundaries.");
        }

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, timeZone).DateTime);
    }
}
