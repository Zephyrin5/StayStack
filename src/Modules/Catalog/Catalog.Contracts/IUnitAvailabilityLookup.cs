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
    ///     Bulk counterpart to GetActiveHoldRangesAsync - which of these
    ///     candidate unit ids have any active hold/booking overlapping
    ///     [<paramref name="checkIn"/>, <paramref name="checkOut"/>). Lets
    ///     GetPropertiesHandler filter search results down to units
    ///     genuinely free for the requested stay, without joining against
    ///     Availability's table directly.
    /// </summary>
    Task<IReadOnlySet<Guid>> GetUnitIdsWithOverlappingHoldAsync(
        IReadOnlyCollection<Guid> unitIds, DateOnly checkIn, DateOnly checkOut, DateTimeOffset now, CancellationToken cancellationToken);

    /// <summary>
    ///     Does this unit have any hold at all right now, active or merely
    ///     un-swept-yet-expired (same loose "held" || "booked" check the
    ///     inline query this replaces always used) - what
    ///     DeleteUnitHandler/DeletePropertyHandler check alongside
    ///     IUnitArchivalGuard's booking check before archiving a unit.
    /// </summary>
    Task<bool> HasActiveHoldForUnitAsync(Guid unitId, CancellationToken cancellationToken);
}

public record ActiveHoldRange
{
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
}
