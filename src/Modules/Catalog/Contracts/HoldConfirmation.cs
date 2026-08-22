using BuildingBlocks.Exceptions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using System.Data;
namespace Catalog.Contracts;

// internal, same reasoning as HostRegistrar - Bookings should only ever
// reach this through IHoldConfirmation, resolved via DI.
internal class HoldConfirmation(AppCatalogDbContext dbContext) : IHoldConfirmation
{
    public async Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken)
    {
        IDbConnection connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        // A single atomic UPDATE...RETURNING, not a transaction spanning
        // this and the Booking insert that follows in Bookings - those are
        // two separate DbContexts/connections. Same "sequential writes,
        // narrow failure window, no distributed transaction" tradeoff
        // BecomeHostHandler already documents and accepts, not a new one.
        // The status/expiry check in the WHERE clause is what makes this
        // safe to call exactly once per hold: a second call (already-booked
        // or expired hold) returns no row.
        const string sql = """
                           UPDATE unit_availability_holds
                           SET status = 'booked'
                           WHERE id = @HoldId AND status = 'held' AND hold_expires_at > now()
                           RETURNING unit_id AS "UnitId", lower(stay_range) AS "CheckIn", upper(stay_range) AS "CheckOut";
                           """;

        ConfirmedHold? confirmedHold = await connection.QuerySingleOrDefaultAsync<ConfirmedHold>(
            new CommandDefinition(sql, new { HoldId = holdId }, cancellationToken: cancellationToken));

        return confirmedHold ?? throw new NotFoundException("Hold", holdId);
    }
}
