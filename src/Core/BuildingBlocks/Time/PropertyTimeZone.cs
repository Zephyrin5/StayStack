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
///         An unusable zone is an error, never a guess: nothing falls back to UTC, because under a
///         UTC+3 market that skew is permissive and loses money.
///     </para>
/// </summary>
public static class PropertyTimeZone
{
    /// <summary>True if the id resolves on this machine, for validators and guards.</summary>
    public static bool IsValid(string? timeZoneId) =>
        !string.IsNullOrWhiteSpace(timeZoneId)
        && TimeZoneInfo.TryFindSystemTimeZoneById(timeZoneId, out _);

    /// <summary>The current date at the given timezone. An instant maps to exactly one local date.</summary>
    /// <exception cref="InvalidOperationException">The id is missing or does not resolve.</exception>
    public static DateOnly Today(TimeProvider timeProvider, string timeZoneId) =>
        ToLocalDate(timeProvider.GetUtcNow(), timeZoneId);

    /// <summary>
    ///     The local date an instant fell on, for a recorded moment or one clock reading shared across
    ///     many bookings. Resolved per call; the BCL caches the lookup.
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
