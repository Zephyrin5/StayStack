namespace Catalog.Contracts;

/// <summary>
///     Lets Catalog ask Availability which units/dates currently have a
///     blocking hold or booking, without ever referencing
///     UnitAvailabilityHold, BookingsDb, or
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
    ///     Bulk counterpart to GetActiveHoldRangesAsync - the unit ids with an active hold or booking
    ///     overlapping [<paramref name="checkIn"/>, <paramref name="checkOut"/>), as a query rather than
    ///     a result.
    ///     <para>
    ///         Unexecuted deliberately: GetPropertiesHandler composes it into its own search, so the
    ///         filter runs in the database against that page's candidate units. Executed here it would
    ///         return every occupied unit on the platform for the window, to filter a page of twenty.
    ///         Composition requires both sides to be on one context, which they are (docs/adr/0003).
    ///     </para>
    /// </summary>
    IQueryable<Guid> BlockedUnitIds(DateOnly checkIn, DateOnly checkOut, DateTimeOffset now);

    /// <summary>
    ///     Is a checkout in progress against this unit right now - what
    ///     DeleteUnitHandler/DeletePropertyHandler ask alongside
    ///     IUnitArchivalGuard's booking check before archiving a unit.
    ///     <para>
    ///         Strictly the claims the booking check cannot see yet: a live unexpired hold, or one
    ///         claimed by a checkout awaiting payment. Both are time-bounded and resolve on their own.
    ///     </para>
    ///     <para>
    ///         Not 'booked' holds: nothing deletes those, so counting them would make a unit with one
    ///         completed stay impossible to archive - whether a booking blocks archival depends on its
    ///         dates, which IUnitArchivalGuard answers. Not expired-but-unswept 'held' rows either:
    ///         ConfirmHoldAsync requires hold_expires_at > now, so one cannot become a booking.
    ///     </para>
    /// </summary>
    Task<bool> HasActiveHoldForUnitAsync(Guid unitId, DateTimeOffset now, CancellationToken cancellationToken);
}

public record ActiveHoldRange
{
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
}
