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
///         <b>At read time an unusable timezone is an error, never a guess.</b>
///         Nothing here falls back to UTC: under a UTC+3 market that is
///         precisely the permissive, money-losing skew this whole change
///         exists to remove, so a wrong answer is worse than no answer. The
///         one deliberate exception lives in the migration that backfilled
///         existing rows, which is a data decision rather than a runtime one.
///     </para>
/// </summary>
public static class PropertyTimeZone
{
    /// <summary>
    ///     True if the id resolves on this machine. Uses Try… rather than
    ///     FindSystemTimeZoneById deliberately: the latter throws
    ///     TimeZoneNotFoundException, which is outside the ArgumentException
    ///     family GlobalExceptionHandler maps to 400, so a domain guard built
    ///     on it would surface a validation failure as a 500.
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
    ///     the anchor is a recorded moment rather than now - e.g. resolving
    ///     which date a booking was cancelled on from its ModifiedAt, so a
    ///     recancel reports the same refund tier the original cancellation
    ///     already queued.
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
