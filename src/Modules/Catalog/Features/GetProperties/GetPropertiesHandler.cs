using BuildingBlocks.Pagination;
using Catalog.Contracts;
using Catalog.Entities;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
namespace Catalog.Features.GetProperties;

public class GetPropertiesHandler(
    AppCatalogDbContext dbContext,
    IUnitAvailabilityLookup availabilityLookup,
    TimeProvider timeProvider,
    HybridCache cache) : IRequestHandler<GetPropertiesRequest, PagedResponse<PropertySummary>>
{
    public async ValueTask<PagedResponse<PropertySummary>> Handle(GetPropertiesRequest request, CancellationToken cancellationToken)
    {
        // Every filter/pagination field that changes the result has to be
        // part of the key - an incomplete key would serve one search's
        // results back for another. A 30s staleness window only means a
        // listing briefly under/over-represents availability, never a
        // double-booking - the exclusion constraint HoldAvailabilityHandler
        // writes through guarantees that, not this cache. Same tradeoff as
        // GetPriceCalendarHandler's own cache.
        //
        // City is normalized the same way the ILIKE query below normalizes
        // it, and the same value is used for both - otherwise "Kuwait City"
        // and "kuwait city" fragment across separate cache entries for what
        // the query treats as identical, and an unnormalized freeform field
        // is unbounded cache-key cardinality for no reason.
        string? normalizedCity = request.City?.Trim().ToLowerInvariant();
        string cacheKey = $"properties:{normalizedCity}:{request.PropertyType}:{request.Guests}:" +
                          $"{request.CheckIn:yyyyMMdd}:{request.CheckOut:yyyyMMdd}:{request.Page}:{request.PageSize}";

        return await cache.GetOrCreateAsync(
            cacheKey,
            async ct => await LoadFromDatabaseAsync(request, normalizedCity, ct),
            new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(30),
                LocalCacheExpiration = TimeSpan.FromSeconds(30)
            },
            cancellationToken: cancellationToken);
    }

    private async Task<PagedResponse<PropertySummary>> LoadFromDatabaseAsync(
        GetPropertiesRequest request, string? normalizedCity, CancellationToken cancellationToken)
    {
        var query = dbContext.Properties.AsNoTracking();

        if (normalizedCity is not null)
        {
            // Case-insensitive contains, not exact match - City is freeform
            // text on both sides, so exact equality would reject "kuwait
            // city" against a stored "Kuwait City" and reject partial
            // search entirely. EscapeLikePattern guards against a search
            // term containing '%'/'_'/'\' being misread as ILIKE wildcards.
            string pattern = $"%{EscapeLikePattern(normalizedCity)}%";
            // The 3-arg overload, not the 2-arg one - Npgsql's 2-arg
            // EF.Functions.ILike emits `ESCAPE ''` (escaping disabled
            // entirely), so EscapeLikePattern's backslashes would be read
            // literally without passing one explicitly. Null-forgiving on
            // p.City, not a null guard - ILIKE against NULL translates to
            // SQL NULL (falsy in WHERE), so Postgres already excludes
            // City-less properties correctly; the compiler just can't see
            // that.
            query = query.Where(p => EF.Functions.ILike(p.City!, pattern, "\\"));
        }

        if (request.PropertyType is not null)
        {
            query = query.Where(p => p.PropertyType == request.PropertyType);
        }

        if (request.Guests is not null || (request.CheckIn is not null && request.CheckOut is not null))
        {
            // One composable query over Units, not two separate
            // property-level Where clauses - capacity and availability must
            // both hold for the SAME unit. Two independent Any() checks
            // would match a property via one unit that fits the guest
            // count and a different unit free for the dates, even if no
            // single unit satisfies both.
            var matchingUnitsQuery = dbContext.Units.AsNoTracking();

            if (request.Guests is not null)
            {
                matchingUnitsQuery = matchingUnitsQuery.Where(u => u.MaxOccupancy >= request.Guests.Value);
            }

            // Materialized here, not left as a composed subquery -
            // unit_availability_holds moved to the Availability module
            // (docs/adr/0004), so the per-date check can no longer be a
            // local correlated Any(). One extra round trip: resolve
            // capacity-matching candidate unit ids, then ask Availability
            // which have a blocking hold/booking for the dates.
            //
            // Unbounded by anything other than how many units match Guests -
            // fine at current scale (docs/adr/0004's Consequences already
            // weighs this round-trip cost), but unlike
            // ReconcileOrphanedBookedHoldsJob's own candidate query, there's
            // no cap here. A Guests-only search against tens of thousands
            // of units would send an equally large id array to Availability -
            // not urgent today, worth a cap if unit count grows.
            List<Guid> candidateUnitIds = await matchingUnitsQuery
                .Select(u => u.Id)
                .ToListAsync(cancellationToken);

            if (request.CheckIn is not null && request.CheckOut is not null && candidateUnitIds.Count > 0)
            {
                IReadOnlySet<Guid> blockedUnitIds = await availabilityLookup.GetUnitIdsWithOverlappingHoldAsync(
                    candidateUnitIds, request.CheckIn.Value, request.CheckOut.Value, timeProvider.GetUtcNow(), cancellationToken);

                candidateUnitIds = [.. candidateUnitIds.Where(id => !blockedUnitIds.Contains(id))];
            }

            query = query.Where(p => dbContext.Units.Any(u => candidateUnitIds.Contains(u.Id) && u.PropertyId == p.Id));
        }

        // Id as a tiebreaker, not a deliberate sort - see docs/adr/0008.
        // If a real sort ever gets added here (price, rating, relevance),
        // it needs to be `.OrderBy(p => p.SomeField).ThenBy(p => p.Id)`,
        // not a bare `.OrderBy(p => p.SomeField)`.
        (List<Property> properties, int totalCount) = await query
            .OrderBy(p => p.Id)
            .ToPagedListAsync(request.Page, request.PageSize, cancellationToken);

        return new PagedResponse<PropertySummary>
        {
            Items = PropertySummaryMapper.Map(properties),
            Page = request.Page,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }

    // Postgres's default ILIKE escape character is '\' - a search term
    // containing a literal '%' or '_' would otherwise be read as a
    // wildcard instead of the character the user actually typed. Order
    // matters: the backslash itself must be escaped first, or escaping
    // '%'/'_' afterward would double-escape the backslashes just added.
    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}
