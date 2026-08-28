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
                // Short TTL rather than event-driven invalidation - a hold
                // created moments ago being briefly invisible on someone
                // else's calendar view is an acceptable tradeoff for not
                // needing a cache-invalidation event bus this early.
                // Revisit if that staleness window ever becomes a real
                // complaint; it does NOT affect correctness of the actual
                // booking - the exclusion constraint is what prevents
                // double-booking, not this cache.
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
        // moved to the Availability module (see docs/adr/0004), so a raw
        // SQL join against it by table name would be exactly the boundary
        // violation ADR-0004 exists to prevent, no matter how proven the
        // query shape already was. Availability answers "which ranges are
        // blocking this unit" through IUnitAvailabilityLookup instead; the
        // per-day containment check below is cheap enough in C# (a handful
        // of ranges against at most a few dozen calendar days) that it
        // isn't worth trying to push back into one SQL statement.
        //
        // Column aliases are cased to match PriceCalendarDayRow's property
        // names exactly (Dapper matches case-insensitively but does NOT
        // strip underscores) - a deliberate per-query choice rather than
        // introducing a project-wide snake_case type map for this first
        // handwritten Dapper query.
        // Raw SQL against `units` (an Entity-derived, soft-delete-governed
        // table) bypasses EF's ApplySoftDeleteQueryFilter entirely, so the
        // status predicate has to be restated by hand here - see
        // docs/adr/0014's Tier 3 rule. Without it an archived unit's
        // calendar was still returned and priced. EntityStatus.Status is
        // stored as its raw integer ordinal, not a string via
        // HasConversion<string>() (unlike Currency) - confirmed against
        // EF's own generated SQL elsewhere (`WHERE p.status <> 2`). Passed
        // as a parameter derived from the enum itself, not a hardcoded `2`
        // literal in the SQL text - this codebase just renumbered Currency
        // specifically because hardcoded ordinals are unsafe to depend on
        // (docs/adr/0015), and EntityStatus has no HasConversion<string>()
        // protection against the same risk if it's ever reordered.
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
