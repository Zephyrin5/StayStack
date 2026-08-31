using Catalog.Contracts;
using Catalog.Domain;
using Catalog.Entities;
using Dapper;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Data;
using System.Data.Common;
namespace Catalog.Features.GetPriceCalendar;

public class GetPriceCalendarHandler(
    AppCatalogDbContext dbContext,
    IUnitAvailabilityLookup availabilityLookup,
    HybridCache cache,
    TimeProvider timeProvider) : IRequestHandler<GetPriceCalendarRequest, GetPriceCalendarResponse>
{
    public async ValueTask<GetPriceCalendarResponse> Handle(
        GetPriceCalendarRequest request,
        CancellationToken cancellationToken)
    {
        string cacheKey = $"price-calendar:{request.UnitId}:{request.From:yyyyMMdd}:{request.To:yyyyMMdd}";

        var days = await cache.GetOrCreateAsync(
            cacheKey,
            async ct => await LoadFromDatabaseAsync(request, ct),
            new HybridCacheEntryOptions
            {
                // Short TTL, not event-driven invalidation - a hold created
                // moments ago being briefly invisible elsewhere is an
                // acceptable tradeoff for not needing a cache-invalidation
                // event bus yet. Doesn't affect booking correctness - the
                // exclusion constraint prevents double-booking, not this
                // cache.
                Expiration = TimeSpan.FromSeconds(30),
                LocalCacheExpiration = TimeSpan.FromSeconds(30)
            },
            cancellationToken: cancellationToken);

        return new GetPriceCalendarResponse { Days = days };
    }

    private async Task<List<PriceCalendarDay>> LoadFromDatabaseAsync(
        GetPriceCalendarRequest request,
        CancellationToken cancellationToken)
    {
        DbConnection connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        // No longer joins unit_availability_holds directly - that table
        // moved to the Availability module (docs/adr/0004), so a raw SQL
        // join by table name would be the exact boundary violation
        // ADR-0004 exists to prevent. Availability answers "which ranges
        // are blocking this unit" through IUnitAvailabilityLookup instead;
        // the per-day containment check below is cheap enough in C# that
        // it isn't worth pushing back into one SQL statement.
        //
        // Column aliases are cased to match PriceCalendarDayRow's property
        // names exactly - Dapper matches case-insensitively but does NOT
        // strip underscores.
        //
        // Raw SQL against `units` (Entity-derived, soft-delete-governed)
        // bypasses EF's ApplySoftDeleteQueryFilter, so the status predicate
        // is restated by hand - see docs/adr/0014's Tier 3 rule; without it
        // an archived unit's calendar was still returned and priced.
        // EntityStatus.Status is stored as a raw integer ordinal, not via
        // HasConversion<string>() like Currency, so ArchivedStatus is
        // passed as a parameter derived from the enum rather than a
        // hardcoded `2` literal - the same ordinal-safety reasoning
        // docs/adr/0015 already applies to Currency.
        const string sql = """
                           SELECT
                               d::date AS "Date",
                               u.base_price AS "BasePrice",
                               u.currency AS "Currency"
                           FROM generate_series(@From::date, @To::date - interval '1 day', interval '1 day') AS d
                           CROSS JOIN units u
                           WHERE u.id = @UnitId AND u.status <> @ArchivedStatus
                           ORDER BY d;
                           """;

        CommandDefinition command = new CommandDefinition(
            sql,
            new
            {
                request.UnitId,
                request.From,
                request.To,
                ArchivedStatus = (int)EntityStatus.Archived
            },
            cancellationToken: cancellationToken);

        var rows = await connection.QueryAsync<PriceCalendarDayRow>(command);

        // Small, low-cardinality reference data - one extra EF round trip
        // per uncached request is an acceptable cost for correctness here,
        // absorbed by this handler's own 30s HybridCache wrapper above.
        List<PricingRule> rules = await dbContext.PricingRules
            .AsNoTracking()
            .Where(r => r.UnitId == request.UnitId)
            .ToListAsync(cancellationToken);

        IReadOnlyList<ActiveHoldRange> blockedRanges = await availabilityLookup.GetActiveHoldRangesAsync(
            request.UnitId, request.From, request.To, timeProvider.GetUtcNow(), cancellationToken);

        return rows
            .Select(row =>
            {
                Money basePrice = Money.Of(row.BasePrice, Enum.Parse<Currency>(row.Currency.Trim()));
                return new PriceCalendarDay
                {
                    Date = row.Date,
                    Price = PricingCalculator.ResolveNightlyPrice(basePrice, row.Date, rules).Amount,
                    IsAvailable = !blockedRanges.Any(r => r.CheckIn <= row.Date && row.Date < r.CheckOut)
                };
            })
            .ToList();
    }

    private sealed record PriceCalendarDayRow
    {
        public DateOnly Date { get; init; }
        public decimal BasePrice { get; init; }
        public string Currency { get; init; } = string.Empty;
    }
}
