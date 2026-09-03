namespace Catalog.Contracts;

/// <summary>
///     Lets Catalog ask Availability which units/dates currently have a
///     blocking hold or booking, without ever referencing
///     UnitAvailabilityHold, AppAvailabilityDbContext, or
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
    ///     time, in place of the single-query SQL join this used to be
    ///     before Availability became its own module.
    /// </summary>
    Task<IReadOnlyList<ActiveHoldRange>> GetActiveHoldRangesAsync(
        Guid unitId, DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    ///     Bulk counterpart to GetActiveHoldRangesAsync - every unit id,
    ///     platform-wide, with an active hold/booking overlapping
    ///     [<paramref name="checkIn"/>, <paramref name="checkOut"/>). Lets
    ///     GetPropertiesHandler filter search results down to units
    ///     genuinely free for the requested stay, without joining against
    ///     Availability's table directly and without first materializing a
    ///     candidate unit id list on Catalog's side to narrow it - the
    ///     result is exactly as large as this window's actual bookings/holds,
    ///     not the whole unit table, and
    ///     <see cref="StaySearchPolicyOptions.MaxStayNights"/> bounds the
    ///     window's width so this can't grow past "every unit ever booked" on
    ///     a wide-open search. Width specifically - how far out the window
    ///     sits has no bearing on how many bookings are inside it.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetBlockedUnitIdsAsync(
        DateOnly checkIn, DateOnly checkOut, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    ///     Is a checkout in progress against this unit right now - what
    ///     DeleteUnitHandler/DeletePropertyHandler ask alongside
    ///     IUnitArchivalGuard's booking check before archiving a unit.
    ///     <para>
    ///         Strictly the claims Availability owns and Bookings cannot yet
    ///         see: a live unexpired hold, or one claimed by a checkout
    ///         awaiting payment. Both are time-bounded and resolve on their
    ///         own.
    ///     </para>
    ///     <para>
    ///         Deliberately <em>not</em> sold ('booked') holds, though it used
    ///         to include them. Nothing ever deletes a booked row, so one
    ///         completed stay left a permanent 'booked' hold and this returned
    ///         true for the rest of that unit's life - making it, and any
    ///         property containing it, impossible to archive years after the
    ///         guest went home. Whether a booking still blocks archival is a
    ///         question about the booking's dates, which is
    ///         IUnitArchivalGuard's to answer (BookingStatus != Cancelled and
    ///         CheckOut >= today) and it already does, for future and current
    ///         stays alike. Availability has no business re-deciding it from
    ///         a row with no date on it.
    ///     </para>
    ///     <para>
    ///         Expired-but-unswept 'held' rows no longer count either. Such a
    ///         hold cannot become a booking - ConfirmHoldAsync requires
    ///         hold_expires_at > now - so blocking archival on one only meant
    ///         waiting for the sweep.
    ///     </para>
    /// </summary>
    Task<bool> HasActiveHoldForUnitAsync(Guid unitId, DateTimeOffset now, CancellationToken cancellationToken);
}

public record ActiveHoldRange
{
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
}
