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
        // results back for a different one. A 30s staleness window here
        // only ever means a listing briefly under- or over-represents live
        // availability, never an actual double-booking - the exclusion
        // constraint HoldAvailabilityHandler writes through is what
        // guarantees that, not this cache. Same accepted tradeoff as
        // GetPriceCalendarHandler's own cache.
        //
        // City is normalized (trimmed/lowercased) the same way the ILIKE
        // query below normalizes it, and the same normalized value is used
        // for both - otherwise "Kuwait City" and "kuwait city" (or with
        // stray whitespace) fragment across separate cache entries for what
        // the query itself treats as an identical search, and an
        // unnormalized freeform field is unbounded cache-key cardinality
        // for no reason.
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
            // Case-insensitive contains, not exact match - City is
            // freeform text on both sides (a host types it when creating a
            // property, a guest types it here), so exact equality would
            // reject "kuwait city" against a stored "Kuwait City" and
            // reject any partial search entirely. EscapeLikePattern guards
            // against a search term that itself contains '%'/'_'/'\' being
            // misread as ILIKE wildcards rather than literal characters.
            string pattern = $"%{EscapeLikePattern(normalizedCity)}%";
            // The 3-arg overload, not the 2-arg one - Npgsql's translation
            // of the 2-arg EF.Functions.ILike emits `ESCAPE ''` (escape
            // processing disabled entirely), not backslash-as-default, so
            // EscapeLikePattern's backslashes would be read as literal
            // characters instead of escapes without passing one explicitly
            // here. Null-forgiving on p.City, not a null guard - ILIKE
            // against a NULL column translates to SQL NULL (falsy in
            // WHERE), so Postgres already excludes properties with no City
            // correctly on its own; the compiler just can't see that C#
            // never touches the value.
            query = query.Where(p => EF.Functions.ILike(p.City!, pattern, "\\"));
        }

        if (request.PropertyType is not null)
        {
            query = query.Where(p => p.PropertyType == request.PropertyType);
        }

        if (request.Guests is not null || (request.CheckIn is not null && request.CheckOut is not null))
        {
            // Built as one composable query over Units, not two separate
            // property-level Where clauses - capacity and availability
            // must both hold for the SAME unit. Two independent Any()
            // checks would let a property match via one unit that fits the
            // guest count and a different unit that's free for the dates,
            // even if no single unit satisfies both.
            var matchingUnitsQuery = dbContext.Units.AsNoTracking();

            if (request.Guests is not null)
            {
                matchingUnitsQuery = matchingUnitsQuery.Where(u => u.MaxOccupancy >= request.Guests.Value);
            }

            // Materialized here rather than left as a composed subquery -
            // unit_availability_holds moved to the Availability module (see
            // docs/adr/0004), so the per-date check below can no longer be
            // a local correlated Any() against it. One extra round trip:
            // resolve capacity-matching candidate unit ids first, then ask
            // Availability which of those have a blocking hold/booking for
            // the requested dates.
            //
            // Unbounded by anything other than how many units match Guests -
            // fine at this app's current scale (see docs/adr/0004's
            // Consequences for the round-trip cost this was already weighed
            // against), but there's no cap here the way
            // ReconcileOrphanedBookedHoldsJob's own candidate query has one
            // (MaxResultsPerRun). A city/property-type-agnostic Guests-only
            // search against tens of thousands of units would send an
            // equally large id array to Availability and build an equally
            // large IN/ANY clause here - not urgent today, worth a cap if
            // this app's unit count ever gets there.
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
