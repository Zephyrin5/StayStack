namespace Catalog.Contracts;

/// <summary>
///     Lets another module veto archiving a Unit without Catalog ever
///     depending on that module's Contracts project - see docs/adr/0004's
///     note on this exact pattern. Catalog is the module every other
///     module is allowed to depend on, never the reverse, so the interface
///     that answers "can this unit be archived" has to live here even
///     though Catalog itself has no idea what a booking is; Bookings
///     implements it (Bookings.Contracts already depends on
///     Catalog.Contracts, so implementing an interface Catalog defines
///     costs Bookings no new dependency) and registers itself against this
///     interface in BookingsServicesRegistration.
/// </summary>
public interface IUnitArchivalGuard
{
    Task<bool> HasActiveBookingForUnitAsync(Guid unitId, DateOnly today, CancellationToken cancellationToken);
}
