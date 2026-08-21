using Dapper;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using System.Data;
using System.Data.Common;
namespace Catalog.Features.GetPriceCalendar;

public class GetPriceCalendarHandler(
    AppCatalogDbContext dbContext,
    HybridCache cache) : IRequestHandler<GetPriceCalendarRequest, GetPriceCalendarResponse>
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

        // Pure indexed read, no per-request pricing computation - once
        // PricingRule exists, its resolved price gets materialized onto
        // this row (or a dedicated calendar table) rather than computed
        // here. For now base_price stands in as the only price source.
        //
        // Column aliases are cased to match PriceCalendarDay's property
        // names exactly (Dapper matches case-insensitively but does NOT
        // strip underscores) - a deliberate per-query choice rather than
        // introducing a project-wide snake_case type map for this first
        // handwritten Dapper query.
        const string sql = """
                           SELECT
                               d::date AS "Date",
                               u.base_price AS "Price",
                               NOT EXISTS (
                                   SELECT 1 FROM unit_availability_holds h
                                   WHERE h.unit_id = @UnitId
                                     AND h.status IN ('held', 'booked')
                                     AND h.stay_range @> d::date
                               ) AS "IsAvailable"
                           FROM generate_series(@From::date, @To::date - interval '1 day', interval '1 day') AS d
                           CROSS JOIN units u
                           WHERE u.id = @UnitId
                           ORDER BY d;
                           """;

        CommandDefinition command = new CommandDefinition(
            sql, new { request.UnitId, request.From, request.To }, cancellationToken: cancellationToken);

        var rows = await connection.QueryAsync<PriceCalendarDay>(command);
        return [.. rows];
    }
}
