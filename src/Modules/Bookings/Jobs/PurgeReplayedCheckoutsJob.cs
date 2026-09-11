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
///         on the request path, so a stopped or misconfigured job can no
///         longer extend the window. What this removes is the stored
///         credential itself, and unlike most retention sweeps that is not
///         about table size. A completed
///         record holds a guest's management token in plaintext - the single
///         deliberate exception to the hash-only rule these tokens otherwise
///         follow - so rows left behind are live credentials readable long
///         after anything can use them.
///     </para>
///     <para>
///         Incomplete records are left alone. Those belong to confirmations
///         still in flight or crashed, and
///         <see cref="ReconcileOrphanedBookingIntentsJob"/> owns them - it
///         removes the reservation as part of unwinding the attempt, on the
///         intent's own grace period, which is far shorter than this window.
///         Deleting them here on a 24-hour clock would race that job for no
///         benefit, and a record deleted out from under a live request would
///         free a key that request is still using.
///     </para>
/// </summary>
public class PurgeReplayedCheckoutsJob(
    AppBookingsDbContext dbContext,
    TimeProvider timeProvider,
    IOptions<BookingLifecyclePolicyOptions> policy)
{
    private const string PurgeSql = """
                                    DELETE FROM checkout_idempotency_records
                                    WHERE completed_at IS NOT NULL AND completed_at <= @Cutoff;
                                    """;

    // Hourly rather than daily, which is the cadence the other retention
    // sweeps use. The difference is what is being retained: a daily sweep
    // would leave a token readable for up to 24 hours past the window it was
    // promised for, doubling the worst-case exposure to buy nothing.
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
