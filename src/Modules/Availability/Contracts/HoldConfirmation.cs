using BuildingBlocks.Exceptions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Data;
using System.Data.Common;
namespace Availability.Contracts;

// internal, same reasoning as Hosts.Contracts' implementations - Bookings
// should only ever reach this through IHoldConfirmation, resolved via DI.
internal class HoldConfirmation(AppAvailabilityDbContext dbContext, TimeProvider timeProvider) : IHoldConfirmation
{
    // Raw shape of the RETURNING row - Currency comes back as its column
    // text, materialized first and turned into a real Money afterward
    // rather than asking Dapper to convert it. Same materialize-first-map-
    // after shape as docs/adr/0006, applied here to Dapper rather than EF.
    private sealed record ConfirmedHoldRow
    {
        public Guid UnitId { get; init; }
        public DateOnly CheckIn { get; init; }
        public DateOnly CheckOut { get; init; }
        public int GuestCount { get; init; }
        public decimal TotalPrice { get; init; }
        public decimal Subtotal { get; init; }
        public string Currency { get; init; } = string.Empty;
        public decimal? LengthOfStayDiscountAmount { get; init; }
    }

    public async Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken)
    {
        DbConnection connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        // A single atomic UPDATE...RETURNING, not a transaction spanning
        // this and the Booking insert that follows in Bookings - see
        // docs/adr/0003 for why this is a cross-module compensating write,
        // not a distributed transaction. The status/expiry check in the
        // WHERE clause makes this safe to call exactly once per hold: a
        // second call (already-booked or expired) returns no row. @Now
        // (the app's TimeProvider), not Postgres' own now() - otherwise
        // the app server and DB server are two different clocks comparing
        // the same expiry.
        const string sql = """
                           UPDATE unit_availability_holds
                           SET status = 'booked', booked_at = @Now
                           WHERE id = @HoldId AND status = 'held' AND hold_expires_at > @Now
                           RETURNING unit_id AS "UnitId", lower(stay_range) AS "CheckIn", upper(stay_range) AS "CheckOut", guest_count AS "GuestCount", total_price AS "TotalPrice", subtotal AS "Subtotal", currency AS "Currency", length_of_stay_discount_amount AS "LengthOfStayDiscountAmount";
                           """;

        ConfirmedHoldRow? row = await connection.QuerySingleOrDefaultAsync<ConfirmedHoldRow>(
            new CommandDefinition(sql, new { HoldId = holdId, Now = timeProvider.GetUtcNow() }, cancellationToken: cancellationToken));

        if (row is null)
        {
            throw new NotFoundException("Hold", holdId);
        }

        Currency currency = Enum.Parse<Currency>(row.Currency.Trim());

        return new ConfirmedHold
        {
            UnitId = row.UnitId,
            CheckIn = row.CheckIn,
            CheckOut = row.CheckOut,
            GuestCount = row.GuestCount,
            TotalPrice = Money.Of(row.TotalPrice, currency),
            Subtotal = row.Subtotal,
            LengthOfStayDiscountAmount = row.LengthOfStayDiscountAmount
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
        // otherwise an immediate release (a cancellation, or a
        // ConfirmBookingHandler rollback moments after the hold was made)
        // would leave the range blocking new holds for whatever was left
        // on the original timer, even though the caller just gave it
        // back. Resetting it makes the row immediately eligible for
        // cleanup instead of waiting it out.
        const string sql = """
                           UPDATE unit_availability_holds
                           SET status = 'held', hold_expires_at = @Now, booked_at = NULL
                           WHERE id = @HoldId AND status = 'booked';
                           """;

        await connection.ExecuteAsync(new CommandDefinition(
            sql, new { HoldId = holdId, Now = timeProvider.GetUtcNow() }, cancellationToken: cancellationToken));
    }
}
