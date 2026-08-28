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
