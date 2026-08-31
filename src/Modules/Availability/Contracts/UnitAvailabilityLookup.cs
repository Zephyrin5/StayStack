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

        // "Booked always blocks, held only while not expired" - materialized
        // as the raw NpgsqlRange first, then unpacked into plain DateOnly
        // bounds in C# after. Not confident LowerBound/UpperBound member
        // access translates inside an EF Select projection, and unpacking
        // client-side is just as cheap for a result set this small.
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
        // Loose check, deliberately - no expiry filter, so an un-swept
        // expired 'held' row still blocks archival until
        // ExpiredHoldsSweepJob reaps it. Not tightened here; that's a
        // separate concern from this lookup's job.
        return dbContext.UnitAvailabilityHolds.AsNoTracking()
            .AnyAsync(h => h.UnitId == unitId && (h.Status == "held" || h.Status == "booked"), cancellationToken);
    }
}
