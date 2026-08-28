using Bookings.Entities;
using Catalog.Contracts;
using Microsoft.EntityFrameworkCore;
namespace Bookings.Contracts;

// internal, same reasoning as BookingLookup - Catalog should only ever
// reach this through IUnitArchivalGuard, resolved via DI. Implements a
// Catalog-defined interface rather than exposing this fact through
// Bookings' own IBookingLookup: Bookings already legitimately depends on
// Catalog.Contracts, but Catalog must never depend back on
// Bookings.Contracts (docs/adr/0004) - defining the interface on the
// Catalog side is what keeps the reference direction correct while the
// query itself still runs against Bookings' own data.
internal class UnitArchivalGuard(AppBookingsDbContext dbContext) : IUnitArchivalGuard
{
    public Task<bool> HasActiveBookingForUnitAsync(Guid unitId, DateOnly today, CancellationToken cancellationToken)
    {
        return dbContext.Bookings.AsNoTracking()
            .AnyAsync(b => b.UnitId == unitId && b.BookingStatus != BookingStatus.Cancelled && b.CheckOut >= today, cancellationToken);
    }
}
