using BuildingBlocks.Pagination;
using Catalog.Contracts;
using Catalog.Entities;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Persistence;
namespace Catalog.Features.GetProperties;

public class GetPropertiesHandler(
    CatalogDb dbContext,
    IUnitAvailabilityLookup availabilityLookup,
    TimeProvider timeProvider,
    HybridCache cache) : IRequestHandler<GetPropertiesRequest, PagedSliceResponse<PropertySummary>>
{
    public async ValueTask<PagedSliceResponse<PropertySummary>> Handle(GetPropertiesRequest request, CancellationToken cancellationToken)
    {
        // No stay-window guard here - GetPropertiesRequestValidator owns both
        // halves of that rule, so an out-of-range window is refused before
        // this handler runs at all, and before a cache key is ever computed
        // for it. The hold path keeps its own lead-time check in the handler
        // because that one needs the property's time zone; this one only
        // needs a clock. See StaySearchPolicyOptions.
        //
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

    private async Task<PagedSliceResponse<PropertySummary>> LoadFromDatabaseAsync(
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

        if (request.CheckIn is not null && request.CheckOut is not null)
        {
            // Holds are Bookings' table (docs/adr/0004), so the blocked units arrive as a query rather
            // than a set and this search composes it: one statement, filtering against the candidate
            // page instead of pulling every occupied unit on the platform back to filter twenty rows.
            IQueryable<Guid> blockedUnitIds = availabilityLookup.BlockedUnitIds(
                request.CheckIn.Value, request.CheckOut.Value, timeProvider.GetUtcNow());

            // One composable Any() over Units, not two separate
            // property-level Where clauses - capacity and availability must
            // both hold for the SAME unit. Two independent Any() checks
            // would match a property via one unit that fits the guest
            // count and a different unit free for the dates, even if no
            // single unit satisfies both.
            query = query.Where(p => dbContext.Units.Any(u =>
                u.PropertyId == p.Id &&
                (request.Guests == null || u.MaxOccupancy >= request.Guests.Value) &&
                !blockedUnitIds.Contains(u.Id)));
        }
        else if (request.Guests is not null)
        {
            // No dates, so no Availability round trip needed at all - this
            // is a plain SQL predicate the planner can index, not a
            // client-side id list round-tripped back as a Contains.
            query = query.Where(p => dbContext.Units.Any(u =>
                u.PropertyId == p.Id && u.MaxOccupancy >= request.Guests.Value));
        }

        // Id as a tiebreaker, not a deliberate sort - see docs/adr/0008.
        // If a real sort ever gets added here (price, rating, relevance),
        // it needs to be `.OrderBy(p => p.SomeField).ThenBy(p => p.Id)`,
        // not a bare `.OrderBy(p => p.SomeField)`.
        // Slice, not ToPagedListAsync: an exact total here meant a second
        // execution of everything above - the ILIKE, and the correlated
        // EXISTS over Units - with no LIMIT to bound it. Nothing consumes
        // the number. The browse UI feeds it straight into a "load more" decision
        // and the sitemap walk stops on an empty page, so both want a boolean,
        // and a boolean costs one extra row on a query already running.
        (List<Property> properties, bool hasNextPage) = await query
            .OrderBy(p => p.Id)
            .ToPagedSliceAsync(request.Page, request.PageSize, cancellationToken);

        return new PagedSliceResponse<PropertySummary>
        {
            Items = PropertySummaryMapper.Map(properties),
            Page = request.Page,
            PageSize = request.PageSize,
            HasNextPage = hasNextPage
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
