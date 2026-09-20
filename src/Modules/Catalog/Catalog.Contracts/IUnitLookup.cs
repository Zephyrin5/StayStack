using SeedWork.ValueObjects;
namespace Catalog.Contracts;

/// <summary>
///     Lets Bookings resolve a unit's price/currency without ever
///     referencing Catalog's own entities or CatalogDb directly -
///     same boundary reasoning as Hosts.Contracts.IHostLookup.
/// </summary>
public interface IUnitLookup
{
    Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken);

    /// <summary>
    ///     Whether the unit is still sellable, answered by a locking read so that a caller holding the
    ///     unit's advisory lock inside a Serializable transaction cannot miss an archive that committed
    ///     while it waited for that lock: the read raises a serialization failure instead, and the
    ///     caller's execution strategy retries on a fresh snapshot (docs/adr/0028).
    /// </summary>
    Task<bool> IsUnitLiveForWriteAsync(Guid unitId, CancellationToken cancellationToken);

    /// <summary>
    ///     Batch counterpart to GetUnitAsync - one round trip for many
    ///     units instead of one call per id. Missing ids are simply absent
    ///     from the result rather than represented as a null entry, so
    ///     callers use TryGetValue/indexer-with-fallback the same way they
    ///     already handle a null single GetUnitAsync result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, UnitSummary>> GetUnitsAsync(IEnumerable<Guid> unitIds, CancellationToken cancellationToken);

    /// <summary>
    ///     The same unit as <see cref="GetUnitAsync"/>, archived ones
    ///     included - for callers asking what a unit <em>was</em> rather than
    ///     whether it can be sold.
    ///     <para>
    ///         Not cosmetic: Reviews went through the sellable lookup, so archiving a unit silently
    ///         took away the right to review every stay that had ever happened in it. Archival is a
    ///         decision about future bookings and must not reach backwards into finished ones.
    ///     </para>
    ///     <para>
    ///         A separate method rather than a flag, so each call site states which question it asks.
    ///         Sellable paths - confirming, redeeming, pricing - keep the filtered one, or an archived
    ///         unit becomes bookable again.
    ///     </para>
    /// </summary>
    Task<UnitSummary?> GetUnitIncludingArchivedAsync(Guid unitId, CancellationToken cancellationToken);

    /// <summary>
    ///     Every unit id across every property a host owns - what
    ///     GetHostBookingsHandler (Bookings) filters its own bookings query
    ///     by, since Bookings has no notion of Property/HostId itself.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUnitIdsForHostAsync(Guid hostId, CancellationToken cancellationToken);

    /// <summary>
    ///     Resolves what a stay would cost right now, plus the unit's max
    ///     occupancy for the same guard HoldAvailabilityHandler (Availability)
    ///     needs before ever touching a hold - one call instead of two,
    ///     avoiding a duplicate Unit read the way GetUnitAsync alone would
    ///     require alongside this. Runs through the same PricingCalculator
    ///     GetPriceCalendarHandler uses internally (see docs/adr/0012), so
    ///     the actual charged price and the public calendar preview can
    ///     never structurally disagree. Null if the unit doesn't exist.
    /// </summary>
    Task<StayPricingResult?> ResolveStayPricingAsync(
        Guid unitId, DateOnly checkIn, DateOnly checkOut, CancellationToken cancellationToken);
}

public record UnitSummary
{
    public Guid Id { get; init; }
    public Dictionary<string, string> Name { get; init; } = new Dictionary<string, string>();
    public int MaxOccupancy { get; init; }
    public Money BasePrice { get; init; }

    // Lets Reviews resolve which property and host a review belongs to from a
    // unit id, at review-creation time.
    public Guid PropertyId { get; init; }
    public Guid HostId { get; init; }

    // The owning property's IANA zone - every business date for this unit
    // resolves in it (docs/adr/0018). Non-nullable: a unit whose property row
    // is missing never reaches a caller of GetUnitAsync, it raises
    // OrphanedUnitException instead.
    public required string TimeZoneId { get; init; }

    // Added for cancellation policies - lets ConfirmBookingHandler
    // snapshot the unit's *current* policy onto the Booking at confirm
    // time, same "the terms they saw are the terms they get" reasoning as
    // TotalPrice/Currency.
    public required CancellationPolicy CancellationPolicy { get; init; }
}

public record StayPricingResult
{
    public int MaxOccupancy { get; init; }
    public Money TotalPrice { get; init; }
    // Money, sharing TotalPrice's currency, so no consumer re-attaches a
    // currency by hand.
    public Money Subtotal { get; init; }
    public Money? LengthOfStayDiscountAmount { get; init; }

    // Carried so HoldAvailabilityHandler can resolve "today" at the
    // property before its check-in guards run - it already awaits this
    // call before computing the date, so no extra round trip.
    public required string TimeZoneId { get; init; }
}
