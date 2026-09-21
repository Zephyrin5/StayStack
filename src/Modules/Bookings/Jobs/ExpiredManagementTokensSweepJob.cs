using Bookings.Contracts;
using Bookings.Entities;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using System.Data;
using TickerQ.Utilities.Base;
namespace Bookings.Jobs;

/// <summary>
///     Deletes management tokens whose booking is far enough past checkout that
///     <see cref="Features.Common.BookingAccessChecker" /> would refuse them anyway.
///     <para>
///         Cleanup, not enforcement: the checker decides on the request path, so a stopped job
///         cannot extend a token's life. What this bounds is how long a live-looking credential
///         sits in the table after it has stopped working, and the table's size.
///     </para>
///     <para>
///         The cutoff is the booking's own, in the property's timezone, because the checker's is
///         (docs/adr/0018). A UTC comparison would delete a token the checker still honours for
///         anyone east of UTC, and honour one it refuses for anyone west.
///     </para>
/// </summary>
public class ExpiredManagementTokensSweepJob(
    BookingsDb dbContext,
    TimeProvider timeProvider,
    IOptions<BookingLifecyclePolicyOptions> policy)
{
    // The same predicate BookingAccessChecker applies, read from the booking rather than the token:
    // a token carries no expiry of its own (see BookingManagementToken).
    private const string SweepSql = $"""
                                    DELETE FROM {BookingsModel.Schema}.booking_management_tokens token
                                    USING {BookingsModel.Schema}.bookings booking
                                    WHERE booking.id = token.booking_id
                                      AND (@Now AT TIME ZONE booking.time_zone_id)::date
                                          > booking.check_out + @LifetimeDays;
                                    """;

    // Daily, offset from the other sweeps so they do not contend for the same connections. Nothing
    // depends on the hour: the rows it removes have been refused by the checker since the day before.
    [TickerFunction(functionName: "Bookings.SweepExpiredManagementTokens", cronExpression: "0 4 * * *")]
    public async Task SweepAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        IDbConnection connection = dbContext.Database.GetDbConnection();

        await connection.ExecuteAsync(new CommandDefinition(
            SweepSql,
            new
            {
                Now = timeProvider.GetUtcNow(),
                LifetimeDays = policy.Value.ManagementTokenLifetimeDaysAfterCheckOut
            },
            cancellationToken: cancellationToken));
    }
}
