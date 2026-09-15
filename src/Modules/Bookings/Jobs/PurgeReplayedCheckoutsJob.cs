using Bookings.Entities;
using Microsoft.Extensions.Options;
using Bookings.Contracts;
using Dapper;
using Microsoft.EntityFrameworkCore;
using System.Data;
using TickerQ.Utilities.Base;
namespace Bookings.Jobs;

/// <summary>
///     Deletes checkout idempotency records once their replay window has
///     passed.
///     <para>
///         Cleanup, not enforcement - ReplayAsync rejects an expired record
///         on the request path, so a stopped or misconfigured job cannot
///         extend the window. A record stores no credential (see
///         <see cref="CheckoutIdempotencyRecord"/>), so what this bounds is
///         the table's size.
///     </para>
/// </summary>
public class PurgeReplayedCheckoutsJob(
    AppBookingsDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<BookingLifecyclePolicyOptions> policy)
{
    private const string PurgeSql = """
                                    DELETE FROM checkout_idempotency_records
                                    WHERE created_at <= @Cutoff;
                                    """;

    // Hourly. ReplayAsync enforces the window, so the cadence only bounds how
    // long expired rows linger.
    [TickerFunction(functionName: "Bookings.PurgeReplayedCheckouts", cronExpression: "20 * * * *")]
    public async Task PurgeAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        IDbConnection connection = dbContext.Database.GetDbConnection();

        await connection.ExecuteAsync(new CommandDefinition(
            PurgeSql,
            new { Cutoff = timeProvider.GetUtcNow() - TimeSpan.FromHours(policy.Value.CheckoutReplayWindowHours) },
            cancellationToken: cancellationToken));
    }
}
