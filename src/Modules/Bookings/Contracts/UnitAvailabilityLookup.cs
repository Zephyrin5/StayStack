using Bookings.Entities;
using Catalog.Contracts;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
namespace Bookings.Contracts;

// internal, same reasoning as HoldConfirmation - Catalog should only ever reach
// this through Catalog.Contracts.IUnitAvailabilityLookup, resolved via DI.
// Implements a Catalog-defined interface rather than exposing this through
// Availability's own Contracts project - Availability already legitimately
// depends on Catalog.Contracts (for IUnitLookup.ResolveStayPricingAsync),
// but Catalog must never depend back on Availability.Contracts. See
// docs/adr/0004.
internal class UnitAvailabilityLookup(AppBookingsDbContext dbContext) : IUnitAvailabilityLookup
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

    public async Task<IReadOnlySet<Guid>> GetBlockedUnitIdsAsync(
        DateOnly checkIn, DateOnly checkOut, DateTimeOffset now, CancellationToken cancellationToken)
    {
        NpgsqlRange<DateOnly> requestedRange = new NpgsqlRange<DateOnly>(checkIn, true, checkOut, false);

        List<Guid> blockedUnitIds = await dbContext.UnitAvailabilityHolds.AsNoTracking()
            .Where(h => h.StayRange.Overlaps(requestedRange) &&
                        (h.Status == HoldStatuses.Booked || h.Status == HoldStatuses.PendingPayment ||
                         (h.Status == HoldStatuses.Held && (h.HoldExpiresAt == null || h.HoldExpiresAt > now))))
            .Select(h => h.UnitId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return blockedUnitIds.ToHashSet();
    }

    public Task<bool> HasActiveHoldForUnitAsync(Guid unitId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Only claims that are still going somewhere. 'booked' rows are never
        // deleted, so matching them would make a unit with one completed stay
        // impossible to archive; booked stays belong to IUnitArchivalGuard (see
        // this method's contract).
        //
        // 'pending_payment' is kept: it is a checkout in flight, bounded by the
        // payment window, and during confirmation it is the only record of one -
        // the hold commits before the Booking row exists (the window
        // docs/adr/0017's intents cover), so Bookings' guard cannot see it yet.
        // Dropping it would let archival land mid-checkout.
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
