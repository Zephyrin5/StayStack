namespace Catalog.Contracts;

/// <summary>
///     Lets Catalog ask Availability which units/dates currently have a
///     blocking hold or booking, without ever referencing
///     UnitAvailabilityHold, AppBookingsDbContext, or
///     unit_availability_holds directly. Declared here (Catalog is upstream
///     of Availability in the module order - see docs/adr/0004) and
///     implemented by Availability, which already depends on
///     Catalog.Contracts for the reverse relationship
///     (IUnitLookup.ResolveStayPricingAsync), so implementing this costs it
///     nothing new.
/// </summary>
public interface IUnitAvailabilityLookup
{
    /// <summary>
    ///     Every active (booked, or held-and-not-yet-expired) hold range for
    ///     one unit overlapping [<paramref name="from"/>, <paramref name="to"/>) -
    ///     what GetPriceCalendarHandler shades as unavailable, one day at a
    ///     time.
    /// </summary>
    Task<IReadOnlyList<ActiveHoldRange>> GetActiveHoldRangesAsync(
        Guid unitId, DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    ///     Bulk counterpart to GetActiveHoldRangesAsync - every unit id,
    ///     platform-wide, with an active hold/booking overlapping
    ///     [<paramref name="checkIn"/>, <paramref name="checkOut"/>). Lets
    ///     GetPropertiesHandler filter search results down to units genuinely
    ///     free for the requested stay, without joining against Bookings'
    ///     table directly and without first materializing a candidate unit id
    ///     list on Catalog's side to narrow it.
    ///     <para>
    ///         <b>This is a known scale boundary, not a bounded read.</b> The
    ///         result is as large as the number of distinct units booked or
    ///         held across the requested window, platform-wide - which grows
    ///         with the platform, not with the request.
    ///         <see cref="StaySearchPolicyOptions.MaxStayNights"/> bounds the
    ///         window's time, not the set's cardinality: a one-night search on a
    ///         large platform can return millions of unit ids, all materialized
    ///         into a HashSet to filter one page.
    ///     </para>
    ///     <para>
    ///         The fix is a denormalized availability read model, filtering in
    ///         the database against the candidate page. Not started - see
    ///         docs/adr/0026 for what triggers it and which cheaper-looking fixes
    ///         are the wrong axis.
    ///     </para>
    /// </summary>
    Task<IReadOnlySet<Guid>> GetBlockedUnitIdsAsync(
        DateOnly checkIn, DateOnly checkOut, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    ///     Is a checkout in progress against this unit right now - what
    ///     DeleteUnitHandler/DeletePropertyHandler ask alongside
    ///     IUnitArchivalGuard's booking check before archiving a unit.
    ///     <para>
    ///         Strictly the claims the booking check cannot yet see: a live
    ///         unexpired hold, or one claimed by a checkout awaiting payment. Both
    ///         are time-bounded and resolve on their own.
    ///     </para>
    ///     <para>
    ///         Not sold ('booked') holds: nothing deletes a booked row, so
    ///         counting them would make a unit with one completed stay, and its
    ///         property, impossible to archive. Whether a booking blocks archival
    ///         depends on its dates, which IUnitArchivalGuard answers
    ///         (BookingStatus != Cancelled and CheckOut >= today).
    ///     </para>
    ///     <para>
    ///         Not expired-but-unswept 'held' rows either: ConfirmHoldAsync
    ///         requires hold_expires_at > now, so such a hold cannot become a
    ///         booking.
    ///     </para>
    /// </summary>
    Task<bool> HasActiveHoldForUnitAsync(Guid unitId, DateTimeOffset now, CancellationToken cancellationToken);
}

public record ActiveHoldRange
{
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
}
