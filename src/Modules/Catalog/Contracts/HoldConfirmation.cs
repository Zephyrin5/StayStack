using BuildingBlocks.Exceptions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using SeedWork.Enums;
using System.Data;
using System.Data.Common;
namespace Catalog.Contracts;

// internal, same reasoning as HostRegistrar - Bookings should only ever
// reach this through IHoldConfirmation, resolved via DI.
internal class HoldConfirmation(AppCatalogDbContext dbContext) : IHoldConfirmation
{
    // Raw shape of the RETURNING row - Currency comes back as its
    // character(3) column text, mapped to the enum after materializing
    // rather than asking Dapper to convert it, same "materialize first, map
    // after" reasoning UnitLookup already documents for its jsonb column.
    private sealed record ConfirmedHoldRow
    {
        public Guid UnitId { get; init; }
        public DateOnly CheckIn { get; init; }
        public DateOnly CheckOut { get; init; }
        public int GuestCount { get; init; }
        public decimal TotalPrice { get; init; }
        public string Currency { get; init; } = string.Empty;
    }

    public async Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken)
    {
        DbConnection connection = dbContext.Database.GetDbConnection();
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
                           RETURNING unit_id AS "UnitId", lower(stay_range) AS "CheckIn", upper(stay_range) AS "CheckOut", guest_count AS "GuestCount", total_price AS "TotalPrice", currency AS "Currency";
                           """;

        ConfirmedHoldRow? row = await connection.QuerySingleOrDefaultAsync<ConfirmedHoldRow>(
            new CommandDefinition(sql, new { HoldId = holdId }, cancellationToken: cancellationToken));

        if (row is null)
        {
            throw new NotFoundException("Hold", holdId);
        }

        return new ConfirmedHold
        {
            UnitId = row.UnitId,
            CheckIn = row.CheckIn,
            CheckOut = row.CheckOut,
            GuestCount = row.GuestCount,
            TotalPrice = row.TotalPrice,
            Currency = Enum.Parse<Currency>(row.Currency.Trim())
        };
    }

    public async Task ReleaseHoldAsync(Guid holdId, CancellationToken cancellationToken)
    {
        DbConnection connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        // hold_expires_at reset to now(), not left at its original value -
        // otherwise a release that happens well within the original 15-
        // minute hold window (an immediate cancellation, or a
        // ConfirmBookingHandler rollback moments after the hold was made)
        // would leave the range still blocking new holds for however long
        // was left on that original timer, even though the caller just
        // explicitly gave the range back. Resetting it makes the row
        // immediately eligible for both HoldAvailabilityHandler's per-unit
        // cleanup and ExpiredHoldsSweepJob, instead of waiting it out.
        const string sql = """
                           UPDATE unit_availability_holds
                           SET status = 'held', hold_expires_at = now()
                           WHERE id = @HoldId AND status = 'booked';
                           """;

        await connection.ExecuteAsync(new CommandDefinition(sql, new { HoldId = holdId }, cancellationToken: cancellationToken));
    }
}
