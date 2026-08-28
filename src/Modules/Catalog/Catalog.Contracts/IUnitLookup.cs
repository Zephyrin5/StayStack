using SeedWork.ValueObjects;
namespace Catalog.Contracts;

/// <summary>
///     Lets Bookings resolve a unit's price/currency without ever
///     referencing Catalog's own entities or AppCatalogDbContext directly -
///     same boundary reasoning as Hosts.Contracts.IHostLookup.
/// </summary>
public interface IUnitLookup
{
    Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken);

    /// <summary>
    ///     Batch counterpart to GetUnitAsync - one round trip for many
    ///     units instead of one call per id. Missing ids are simply absent
    ///     from the result rather than represented as a null entry, so
    ///     callers use TryGetValue/indexer-with-fallback the same way they
    ///     already handle a null single GetUnitAsync result.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, UnitSummary>> GetUnitsAsync(IEnumerable<Guid> unitIds, CancellationToken cancellationToken);

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

    // Added for Reviews - resolving "which property/host does this review
    // belong to" from a unit id, once at review-creation time, without a
    // second lookup interface.
    public Guid PropertyId { get; init; }
    public Guid HostId { get; init; }

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
    public decimal Subtotal { get; init; }
    public Money? LengthOfStayDiscountAmount { get; init; }
}
