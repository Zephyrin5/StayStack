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
///         extend the window. The row holds no credential (see
///         <see cref="CheckoutIdempotencyRecord"/>), but the key it answers
///         mints one on demand for as long as it lives, so what this bounds
///         is the window's reach as well as the table's size (docs/adr/0022).
///     </para>
/// </summary>
public class PurgeReplayedCheckoutsJob(
    BookingsDb dbContext,
    TimeProvider timeProvider,
    IOptions<BookingLifecyclePolicyOptions> policy)
{
    private const string PurgeSql = $"""
                                    DELETE FROM {BookingsModel.Schema}.checkout_idempotency_records
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
