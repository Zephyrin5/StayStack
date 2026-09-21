using Bookings.Entities;
using Catalog.Contracts;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
namespace Bookings.Contracts;

// internal, same reasoning as HoldConfirmation - Catalog should only ever reach
// this through Catalog.Contracts.IUnitAvailabilityLookup, resolved via DI.
// Implements a Catalog-defined interface rather than exposing it through
// Bookings.Contracts: Bookings already depends on Catalog.Contracts (for
// IUnitLookup.ResolveStayPricingAsync), and Catalog is upstream, so it must
// never depend back. See docs/adr/0004.
internal class UnitAvailabilityLookup(BookingsDb dbContext) : IUnitAvailabilityLookup
{
    public async Task<IReadOnlyList<ActiveHoldRange>> GetActiveHoldRangesAsync(
        Guid unitId, DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        NpgsqlRange<DateOnly> window = new NpgsqlRange<DateOnly>(from, true, to, false);

        // "Booked always blocks, held only while not expired" - materialized
        // as the raw NpgsqlRange first, then unpacked into plain DateOnly
        // bounds in C# after. Not confident LowerBound/UpperBound member
        // access translates inside an EF Select projection, and unpacking
        // client-side is just as cheap for a result set this small.
        List<NpgsqlRange<DateOnly>> ranges = await dbContext.UnitAvailabilityHolds.AsNoTracking()
            .Where(h => h.UnitId == unitId &&
                        h.StayRange.Overlaps(window) &&
                        (h.Status == HoldStatuses.Booked || h.Status == HoldStatuses.PendingPayment ||
                         (h.Status == HoldStatuses.Held && (h.HoldExpiresAt == null || h.HoldExpiresAt > now))))
            .Select(h => h.StayRange)
            .ToListAsync(cancellationToken);

        return ranges
            .Select(r => new ActiveHoldRange { CheckIn = r.LowerBound, CheckOut = r.UpperBound })
            .ToList();
    }

    public IQueryable<Guid> BlockedUnitIds(DateOnly checkIn, DateOnly checkOut, DateTimeOffset now)
    {
        NpgsqlRange<DateOnly> requestedRange = new NpgsqlRange<DateOnly>(checkIn, true, checkOut, false);

        // Returned unexecuted so the caller's query carries it (see the contract). No Distinct: the
        // caller composes it under a Contains/Any, where duplicates cost nothing.
        return dbContext.UnitAvailabilityHolds.AsNoTracking()
            .Where(h => h.StayRange.Overlaps(requestedRange) &&
                        (h.Status == HoldStatuses.Booked || h.Status == HoldStatuses.PendingPayment ||
                         (h.Status == HoldStatuses.Held && (h.HoldExpiresAt == null || h.HoldExpiresAt > now))))
            .Select(h => h.UnitId);
    }

    public Task<bool> HasActiveHoldForUnitAsync(Guid unitId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Only claims that are still going somewhere. 'booked' rows are never
        // deleted, so matching them would make a unit with one completed stay
        // impossible to archive; booked stays belong to IUnitArchivalGuard (see
        // this method's contract).
        //
        // 'pending_payment' is kept: it is a checkout awaiting payment, bounded by
        // the payment window. It commits with its Booking, which the booking guard
        // also sees; counting it here keeps this guard complete on its own rather
        // than dependent on that one.
        //
        // Null HoldExpiresAt reads as active, matching the two range queries
        // above - an absent expiry is not an elapsed one.
        return dbContext.UnitAvailabilityHolds.AsNoTracking()
            .AnyAsync(h => h.UnitId == unitId &&
                           (h.Status == HoldStatuses.PendingPayment ||
                            (h.Status == HoldStatuses.Held &&
                             (h.HoldExpiresAt == null || h.HoldExpiresAt > now))), cancellationToken);
    }
}
