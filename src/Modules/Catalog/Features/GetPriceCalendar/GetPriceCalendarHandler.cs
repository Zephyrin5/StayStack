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
    CatalogDb dbContext,
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
        // EF reference-counts explicit opens: a connection opened this way
        // stays checked out of the pool until a matching close, or until the
        // DbContext is disposed. In a request scope that is the end of the
        // request and the cost is invisible, which is exactly why it is worth
        // closing here rather than relying on it - the same few lines in a
        // background job, whose scope can outlive many queries, would pin a
        // pooled connection for the job's lifetime.
        //
        // Closed only if opened here. When something upstream already has the
        // connection open - an ambient transaction, most likely - it owns the
        // lifetime and EF's count, and closing on its behalf would end it
        // early.
        DbConnection connection = dbContext.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            return await QueryCalendarAsync(request, connection, cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    // Kept whole rather than closing straight after the Dapper call: the EF
    // read below runs on this same connection, so releasing it early would
    // only make EF reopen one.
    private async Task<List<PriceCalendarDay>> QueryCalendarAsync(
        GetPriceCalendarRequest request,
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        // Holds are Bookings' table, so a raw join by table name would cross
        // the module boundary (docs/adr/0004). IUnitAvailabilityLookup answers
        // which ranges block this unit; the per-day containment check below is
        // cheap enough in C#.
        //
        // Column aliases are cased to match PriceCalendarDayRow's property
        // names exactly - Dapper matches case-insensitively but does NOT
        // strip underscores.
        //
        // Raw SQL against `units` (Entity-derived, soft-delete-governed)
        // bypasses EF's ApplySoftDeleteQueryFilter, so the status predicate
        // is restated by hand (docs/adr/0014's Tier 3 rule); without it an
        // archived unit's calendar is returned and priced.
        // EntityStatus.Status is stored as a raw integer ordinal, so
        // ArchivedStatus is passed as a parameter derived from the enum rather
        // than a hardcoded `2` literal.
        const string sql = $"""
                           SELECT
                               d::date AS "Date",
                               u.base_price AS "BasePrice",
                               u.currency AS "Currency"
                           FROM generate_series(@From::date, @To::date - interval '1 day', interval '1 day') AS d
                           CROSS JOIN {CatalogModel.Schema}.units u
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
                Money basePrice = Money.Of(row.BasePrice, row.Currency);
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
        // Currency, not string: CurrencyTypeHandler converts the character(3)
        // column, as DateOnlyTypeHandler does for Date above.
        public Currency Currency { get; init; }
    }
}
