using Persistence;
using Catalog.Domain;
using Catalog.Entities;
using Catalog.Exceptions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SeedWork.Enums;
namespace Catalog.Contracts;

// internal, same reasoning as Hosts.Contracts' implementations - Bookings
// should only ever reach this through IUnitLookup, resolved via DI.
internal class UnitLookup(CatalogDb dbContext) : IUnitLookup
{
    public async Task<bool> IsUnitLiveForWriteAsync(Guid unitId, CancellationToken cancellationToken)
    {
        // A snapshot-isolated caller sees the unit as of its transaction's first statement, which is
        // taken before it waits on the unit's advisory lock - so an archive that committed during the
        // wait is invisible to a plain read. FOR SHARE is what makes it visible: Postgres refuses a
        // locking read of a row updated by a transaction that committed after the caller's snapshot,
        // raising 40001 for the execution strategy to retry on a fresh one (docs/adr/0028).
        IDbContextTransaction transaction = dbContext.Database.CurrentTransaction
                                            ?? throw new InvalidOperationException(
                                                $"{nameof(IsUnitLiveForWriteAsync)} must run inside a transaction. Outside one the row " +
                                                "lock is released before the write it guards, and the answer is a plain stale read.");

        // Raw SQL because EF has no locking-read API. `status <> @ArchivedStatus` is the soft-delete
        // filter restated by hand; SoftDeleteFilterShapeTests fails if the filter grows past what this
        // copy matches.
        return await dbContext.Database.GetDbConnection().ExecuteScalarAsync<int?>(new CommandDefinition(
            $"SELECT 1 FROM {CatalogModel.Schema}.units WHERE id = @UnitId AND {SoftDelete.NotArchived} FOR SHARE",
            new { UnitId = unitId },
            transaction.GetDbTransaction(),
            cancellationToken: cancellationToken)) is not null;
    }

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
        // IgnoreQueryFilters: every caller is decorating bookings that have
        // already happened, and a stay does not stop having taken place in a
        // unit because the host has since archived it. With the filter on,
        // those rows silently dropped out and the booking rendered with a
        // blank name.
        var rows = await (
            from unit in dbContext.Units.AsNoTracking().IgnoreQueryFilters()
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
        // IgnoreQueryFilters, on both the units and the properties they are
        // matched against. This answers "which units has this host ever had",
        // and its callers use it to find that host's bookings - so with the
        // soft-delete filter on, archiving a unit retroactively removed its
        // completed bookings from the host's own list. The bookings still
        // happened and the money still moved; only the listing forgot.
        return await dbContext.Units.AsNoTracking().IgnoreQueryFilters()
            .Where(u => dbContext.Properties.IgnoreQueryFilters()
                .Where(p => p.HostId == hostId).Select(p => p.Id).Contains(u.PropertyId))
            .Select(u => u.Id)
            .ToListAsync(cancellationToken);
    }

    public async Task<UnitSummary?> GetUnitIncludingArchivedAsync(Guid unitId, CancellationToken cancellationToken)
    {
        // The same query as GetUnitAsync with the soft-delete filter lifted.
        // Kept as a separate method rather than a flag on that one so each
        // call site says which question it is asking: "can this be sold" or
        // "what was this".
        var row = await (
            from unit in dbContext.Units.AsNoTracking().IgnoreQueryFilters()
            where unit.Id == unitId
            join property in dbContext.Properties.AsNoTracking().IgnoreQueryFilters() on unit.PropertyId equals property.Id into properties
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
            Subtotal = breakdown.Subtotal,
            LengthOfStayDiscountAmount = breakdown.LengthOfStayDiscountAmount,
            TimeZoneId = row.TimeZoneId
        };
    }
}
