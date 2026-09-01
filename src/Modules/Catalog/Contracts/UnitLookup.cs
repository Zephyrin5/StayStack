using Catalog.Domain;
using Catalog.Entities;
using Catalog.Exceptions;
using Microsoft.EntityFrameworkCore;
namespace Catalog.Contracts;

// internal, same reasoning as Hosts.Contracts' implementations - Bookings
// should only ever reach this through IUnitLookup, resolved via DI.
internal class UnitLookup(AppCatalogDbContext dbContext) : IUnitLookup
{
    public async Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken)
    {
        // Materialize first, map after - see docs/adr/0006. Left-joined with
        // Properties, not inner, so a unit whose Property row is missing is
        // still *seen* here and can be reported as the data-integrity
        // violation it is, rather than silently vanishing from every result.
        //
        // Note this branch is not reachable for an *archived* property:
        // DeletePropertyHandler is the only caller of Property.Archive and it
        // archives every unit beneath the property in the same
        // SaveChangesAsync, so the soft-delete filter removes those units at
        // the query root before this join is reached. Only a hard-deleted or
        // never-existent Property row gets here.
        var row = await (
            from unit in dbContext.Units.AsNoTracking()
            where unit.Id == unitId
            join property in dbContext.Properties.AsNoTracking() on unit.PropertyId equals property.Id into properties
            from property in properties.DefaultIfEmpty()
            select new
            {
                unit,
                HostId = (Guid?)(property != null ? property.HostId : null),
                TimeZoneId = property != null ? property.TimeZoneId : null
            }
        ).SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        // Throws rather than defaulting: the caller wants this one unit, and
        // without its property there is no host to authorize against and no
        // timezone to resolve dates in. A silent Guid.Empty/UTC here is
        // exactly the class of quiet wrong answer docs/adr/0018 exists to
        // remove. The batch overload deliberately differs - see there.
        if (row.HostId is null || row.TimeZoneId is null)
        {
            throw new OrphanedUnitException(row.unit.Id, row.unit.PropertyId);
        }

        return new UnitSummary
        {
            Id = row.unit.Id,
            Name = new Dictionary<string, string>(row.unit.Name.Values),
            MaxOccupancy = row.unit.MaxOccupancy,
            BasePrice = row.unit.BasePrice,
            PropertyId = row.unit.PropertyId,
            HostId = row.HostId.Value,
            TimeZoneId = row.TimeZoneId,
            CancellationPolicy = row.unit.CancellationPolicy
        };
    }

    public async Task<IReadOnlyDictionary<Guid, UnitSummary>> GetUnitsAsync(IEnumerable<Guid> unitIds, CancellationToken cancellationToken)
    {
        List<Guid> ids = [.. unitIds];

        // Same materialize-first-map-after constraint, and same left-join
        // reasoning, as GetUnitAsync above.
        var rows = await (
            from unit in dbContext.Units.AsNoTracking()
            where ids.Contains(unit.Id)
            join property in dbContext.Properties.AsNoTracking() on unit.PropertyId equals property.Id into properties
            from property in properties.DefaultIfEmpty()
            select new
            {
                unit,
                HostId = (Guid?)(property != null ? property.HostId : null),
                TimeZoneId = property != null ? property.TimeZoneId : null
            }
        ).ToListAsync(cancellationToken);

        // Omits orphans rather than throwing, unlike the single-unit overload.
        // Every caller here is a list endpoint (GetBookingsForHost,
        // GetHostBookings, GetMyBookings, ListMyReviewableBookings) where one
        // bad row must not fail the whole page, and they already treat a
        // missing entry as normal - IReadOnlyDictionary expresses absence, and
        // ListMyReviewableBookingsHandler already TryGetValues with a
        // fallback.
        return rows
            .Where(row => row.HostId is not null && row.TimeZoneId is not null)
            .ToDictionary(
                row => row.unit.Id,
                row => new UnitSummary
                {
                    Id = row.unit.Id,
                    Name = new Dictionary<string, string>(row.unit.Name.Values),
                    MaxOccupancy = row.unit.MaxOccupancy,
                    BasePrice = row.unit.BasePrice,
                    PropertyId = row.unit.PropertyId,
                    HostId = row.HostId!.Value,
                    TimeZoneId = row.TimeZoneId!,
                    CancellationPolicy = row.unit.CancellationPolicy
                });
    }

    public async Task<IReadOnlyList<Guid>> GetUnitIdsForHostAsync(Guid hostId, CancellationToken cancellationToken)
    {
        return await dbContext.Units.AsNoTracking()
            .Where(u => dbContext.Properties.Where(p => p.HostId == hostId).Select(p => p.Id).Contains(u.PropertyId))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<StayPricingResult?> ResolveStayPricingAsync(
        Guid unitId, DateOnly checkIn, DateOnly checkOut, CancellationToken cancellationToken)
    {
        // LEFT join, deliberately, matching GetUnitAsync - an inner join would
        // make an orphaned unit produce no row at all, so this returns null
        // and HoldAvailabilityHandler reports "unit not found" for a unit that
        // exists. One data-integrity violation would then give two different
        // answers depending on which entry point the guest hit, with the hold
        // path getting the one that hides it.
        var row = await (
            from unit in dbContext.Units.AsNoTracking()
            where unit.Id == unitId
            join property in dbContext.Properties.AsNoTracking() on unit.PropertyId equals property.Id into properties
            from property in properties.DefaultIfEmpty()
            select new { unit, TimeZoneId = property != null ? property.TimeZoneId : null }
        ).SingleOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        if (row.TimeZoneId is null)
        {
            throw new OrphanedUnitException(row.unit.Id, row.unit.PropertyId);
        }

        List<PricingRule> rules = await dbContext.PricingRules.AsNoTracking()
            .Where(r => r.UnitId == unitId)
            .ToListAsync(cancellationToken);

        StayPriceBreakdown breakdown = PricingCalculator.ResolveStayTotal(row.unit.BasePrice, checkIn, checkOut, rules);

        return new StayPricingResult
        {
            MaxOccupancy = row.unit.MaxOccupancy,
            TotalPrice = breakdown.Total,
            Subtotal = breakdown.Subtotal.Amount,
            LengthOfStayDiscountAmount = breakdown.LengthOfStayDiscountAmount,
            TimeZoneId = row.TimeZoneId
        };
    }
}
