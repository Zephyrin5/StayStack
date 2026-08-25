using BuildingBlocks.Pagination;
using Catalog.Entities;
using Mediator;
using Microsoft.EntityFrameworkCore;
using NpgsqlTypes;
namespace Catalog.Features.GetProperties;

public class GetPropertiesHandler(AppCatalogDbContext dbContext, TimeProvider timeProvider)
    : IRequestHandler<GetPropertiesRequest, PagedResponse<PropertySummary>>
{
    public async ValueTask<PagedResponse<PropertySummary>> Handle(GetPropertiesRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.Properties.AsNoTracking();

        if (request.City is not null)
        {
            // Case-insensitive contains, not exact match - City is
            // freeform text on both sides (a host types it when creating a
            // property, a guest types it here), so exact equality would
            // reject "kuwait city" against a stored "Kuwait City" and
            // reject any partial search entirely. EscapeLikePattern guards
            // against a search term that itself contains '%'/'_'/'\' being
            // misread as ILIKE wildcards rather than literal characters.
            string pattern = $"%{EscapeLikePattern(request.City)}%";
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
            var matchingUnits = dbContext.Units.AsNoTracking();

            if (request.Guests is not null)
            {
                matchingUnits = matchingUnits.Where(u => u.MaxOccupancy >= request.Guests.Value);
            }

            if (request.CheckIn is not null && request.CheckOut is not null)
            {
                // Half-open [CheckIn, CheckOut) - matches the range
                // HoldAvailabilityHandler writes and GetPriceCalendarHandler
                // reads. Same "booked always blocks, held only while not
                // expired" predicate as GetPriceCalendarHandler's raw SQL -
                // see docs/adr/0010 for why unit_availability_holds itself
                // is a Dapper-owned table even though plain reads like this
                // one go through EF's normal LINQ translation.
                NpgsqlRange<DateOnly> requestedRange =
                    new NpgsqlRange<DateOnly>(request.CheckIn.Value, true, request.CheckOut.Value, false);
                DateTimeOffset now = timeProvider.GetUtcNow();

                matchingUnits = matchingUnits.Where(u => !dbContext.UnitAvailabilityHolds.Any(h =>
                    h.UnitId == u.Id &&
                    h.StayRange.Overlaps(requestedRange) &&
                    (h.Status == "booked" || (h.Status == "held" && (h.HoldExpiresAt == null || h.HoldExpiresAt > now)))));
            }

            query = query.Where(p => matchingUnits.Any(u => u.PropertyId == p.Id));
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
