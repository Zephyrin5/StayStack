using Catalog.Contracts;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
namespace Availability.Contracts;

// internal, same reasoning as HoldLookup - Catalog should only ever reach
// this through Catalog.Contracts.IUnitAvailabilityLookup, resolved via DI.
// Implements a Catalog-defined interface rather than exposing this through
// Availability's own Contracts project - Availability already legitimately
// depends on Catalog.Contracts (for IUnitLookup.ResolveStayPricingAsync),
// but Catalog must never depend back on Availability.Contracts. See
// docs/adr/0004.
internal class UnitAvailabilityLookup(AppAvailabilityDbContext dbContext) : IUnitAvailabilityLookup
{
    public async Task<IReadOnlyList<ActiveHoldRange>> GetActiveHoldRangesAsync(
        Guid unitId, DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken cancellationToken)
    {
        NpgsqlRange<DateOnly> window = new NpgsqlRange<DateOnly>(from, true, to, false);

        // Same "booked always blocks, held only while not expired"
        // predicate GetPriceCalendarHandler's own raw SQL used before this
        // table moved to its own module. Materialized as the raw
        // NpgsqlRange first, then unpacked into plain DateOnly bounds in
        // C# after - not confident LowerBound/UpperBound member access
        // translates inside an EF Select projection, and there's no need
        // to find out when materializing the whole range column and
        // unpacking client-side is just as cheap for a per-unit result set
        // this small.
        List<NpgsqlRange<DateOnly>> ranges = await dbContext.UnitAvailabilityHolds.AsNoTracking()
            .Where(h => h.UnitId == unitId &&
                        h.StayRange.Overlaps(window) &&
                        (h.Status == "booked" || (h.Status == "held" && (h.HoldExpiresAt == null || h.HoldExpiresAt > now))))
            .Select(h => h.StayRange)
            .ToListAsync(cancellationToken);

        return ranges
            .Select(r => new ActiveHoldRange { CheckIn = r.LowerBound, CheckOut = r.UpperBound })
            .ToList();
    }

    public async Task<IReadOnlySet<Guid>> GetUnitIdsWithOverlappingHoldAsync(
        IReadOnlyCollection<Guid> unitIds, DateOnly checkIn, DateOnly checkOut, DateTimeOffset now, CancellationToken cancellationToken)
    {
        NpgsqlRange<DateOnly> requestedRange = new NpgsqlRange<DateOnly>(checkIn, true, checkOut, false);

        List<Guid> blockedUnitIds = await dbContext.UnitAvailabilityHolds.AsNoTracking()
            .Where(h => unitIds.Contains(h.UnitId) &&
                        h.StayRange.Overlaps(requestedRange) &&
                        (h.Status == "booked" || (h.Status == "held" && (h.HoldExpiresAt == null || h.HoldExpiresAt > now))))
            .Select(h => h.UnitId)
            .Distinct()
            .ToListAsync(cancellationToken);

        return blockedUnitIds.ToHashSet();
    }

    public Task<bool> HasActiveHoldForUnitAsync(Guid unitId, CancellationToken cancellationToken)
    {
        // Same loose check the inline DeleteUnitHandler query always used -
        // no expiry filter, so an un-swept expired 'held' row still blocks
        // archival until ExpiredHoldsSweepJob reaps it. Preserved exactly,
        // not "fixed", since tightening it wasn't part of this move.
        return dbContext.UnitAvailabilityHolds.AsNoTracking()
            .AnyAsync(h => h.UnitId == unitId && (h.Status == "held" || h.Status == "booked"), cancellationToken);
    }
}
