using Dapper;
using Microsoft.EntityFrameworkCore;
using System.Data;
using TickerQ.Utilities.Base;
namespace Catalog.Jobs;

/// <summary>
///     HoldAvailabilityHandler already deletes a unit's own stale holds
///     right before it would otherwise be blocked by one - but that only
///     fires for a unit someone actually retries. A range nobody retries
///     just sits there as an orphaned 'held' row forever. Harmless
///     (GetPriceCalendarHandler already treats an expired hold as
///     available, and the exclusion constraint only ever blocks a new hold
///     for that exact unit/range again), but still worth sweeping
///     periodically so the table doesn't accumulate dead rows indefinitely.
/// </summary>
public class ExpiredHoldsSweepJob(AppCatalogDbContext dbContext, TimeProvider timeProvider)
{
    private const string CleanupSql = """
                                       DELETE FROM unit_availability_holds
                                       WHERE status = 'held' AND hold_expires_at <= @Now;
                                       """;

    [TickerFunction(functionName: "Catalog.SweepExpiredHolds", cronExpression: "*/5 * * * *")]
    public async Task SweepAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        IDbConnection connection = dbContext.Database.GetDbConnection();

        await connection.ExecuteAsync(new CommandDefinition(
            CleanupSql,
            new { Now = timeProvider.GetUtcNow() },
            cancellationToken: cancellationToken));
    }
}
