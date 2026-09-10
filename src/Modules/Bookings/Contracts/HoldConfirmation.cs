using Bookings.Entities;
using BuildingBlocks.Exceptions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Data;
using System.Data.Common;
namespace Bookings.Contracts;

// internal, same reasoning as Hosts.Contracts' implementations - Bookings
// should only ever reach this through IHoldConfirmation, resolved via DI.
internal class HoldConfirmation(AppBookingsDbContext dbContext, TimeProvider timeProvider) : IHoldConfirmation
{
    // Raw shape of the RETURNING row, materialized first and assembled into
    // Money afterward - the same materialize-first-map-after shape as
    // docs/adr/0006, applied to Dapper rather than EF. The three amounts
    // share one currency column, which is why they cannot be mapped straight
    // onto Money here.
    //
    // Currency is the enum rather than its column text: CurrencyTypeHandler
    // converts it, so the parse that used to sit at the use site is gone.
    private sealed record ConfirmedHoldRow
    {
        public Guid UnitId { get; init; }
        public DateOnly CheckIn { get; init; }
        public DateOnly CheckOut { get; init; }
        public int GuestCount { get; init; }
        public decimal TotalPrice { get; init; }
        public decimal Subtotal { get; init; }
        public Currency Currency { get; init; }
        public decimal? LengthOfStayDiscountAmount { get; init; }
    }

    public async Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken)
    {
        // Closed in the finally below, and only if opened here. EF
        // reference-counts explicit opens, so without the close the
        // connection stays checked out until the DbContext is disposed
        // rather than going back to the pool after this statement - fine
        // inside a request scope, and a held pooled connection anywhere a
        // scope is longer-lived, such as a job. When something upstream
        // already has the connection open (an ambient transaction) it owns
        // the lifetime, so this leaves it alone.
        DbConnection connection = dbContext.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            return await ExecuteConfirmAsync(connection, holdId, cancellationToken);
        }
        finally
        {
            if (openedHere)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    private async Task<ConfirmedHold> ExecuteConfirmAsync(
        DbConnection connection, Guid holdId, CancellationToken cancellationToken)
    {
        // A single atomic UPDATE...RETURNING, not a transaction spanning
        // this and the Booking insert that follows in Bookings - see
        // docs/adr/0003 for why this is a cross-module compensating write,
        // not a distributed transaction. The status/expiry check in the
        // WHERE clause makes this safe to call exactly once per hold: a
        // second call (already-booked or expired) returns no row. @Now
        // (the app's TimeProvider), not Postgres' own now() - otherwise
        // the app server and DB server are two different clocks comparing
        // the same expiry.
        // 'pending_payment', not 'booked': submitting a checkout form is not
        // paying, and this used to mint permanent inventory on that basis -
        // a row nothing reclaimed, holding its range through the exclusion
        // constraint forever, reachable anonymously. 'booked' is now written
        // only by MarkHoldPaidAsync, from the payment-confirmation path.
        //
        // booked_at stays null for the same reason - it records when the
        // range was actually sold.
        //
        // client_key is NOT cleared here, unlike the booked transition,
        // which does clear it. It is a caller's network address and the
        // retention argument for nulling it still stands; what changed is
        // when. Holding it through the payment window keeps it available to
        // any per-client reasoning during exactly the window it describes,
        // and the row loses it on payment or is released outright by
        // Bookings' expiry job. Note the cap does not read it here: the cap
        // counts 'held' only, deliberately, since capping bookings in
        // progress by an IP-derived key would deny checkout to everyone
        // behind one NAT.
        const string sql = $"""
                            UPDATE unit_availability_holds
                            SET status = '{HoldStatuses.PendingPayment}'
                            WHERE id = @HoldId AND status = '{HoldStatuses.Held}' AND hold_expires_at > @Now
                            RETURNING unit_id AS "UnitId", lower(stay_range) AS "CheckIn", upper(stay_range) AS "CheckOut", guest_count AS "GuestCount", total_price AS "TotalPrice", subtotal AS "Subtotal", currency AS "Currency", length_of_stay_discount_amount AS "LengthOfStayDiscountAmount";
                            """;

        ConfirmedHoldRow? row = await connection.QuerySingleOrDefaultAsync<ConfirmedHoldRow>(
            new CommandDefinition(sql, new { HoldId = holdId, Now = timeProvider.GetUtcNow() }, cancellationToken: cancellationToken));

        if (row is null)
        {
            throw new NotFoundException("Hold", holdId);
        }

        Currency currency = row.Currency;

        return new ConfirmedHold
        {
            UnitId = row.UnitId,
            CheckIn = row.CheckIn,
            CheckOut = row.CheckOut,
            GuestCount = row.GuestCount,
            TotalPrice = Money.Of(row.TotalPrice, currency),
            // The one place the hold's single currency column is paired back
            // onto its three amounts. Callers receive Money and never repeat
            // this - ConfirmBookingHandler used to redo it by hand.
            Subtotal = Money.Of(row.Subtotal, currency),
            LengthOfStayDiscountAmount = row.LengthOfStayDiscountAmount is { } discount
                ? Money.Of(discount, currency)
                : null
        };
    }

    public async Task<bool> MarkHoldPaidAsync(Guid holdId, CancellationToken cancellationToken)
    {
        DbConnection connection = dbContext.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        // The only writer of 'booked', and the reason that state is reachable
        // at all rather than dead: it is driven from the payment-confirmation
        // path (Transactions' ConfirmBookingPaymentOutboxMessage ->
        // IBookingPaymentConfirmation), which is admin-reachable today via
        // MarkTransactionSucceeded, so the transition is exercised now
        // instead of waiting on a payment provider.
        //
        // booked_at is set here rather than at checkout - it records when the
        // range was sold, which is this moment and not the earlier one.
        //
        // client_key is cleared here, the retention argument that used to
        // apply at checkout: it is a caller's network address, nothing reads
        // it once the row is past the payment window, and this row now
        // outlives the hold by years. Nothing restores it - ReleaseHoldAsync
        // resets hold_expires_at to now, putting the row outside the cap's
        // WHERE clause regardless.
        //
        // Idempotent: 'booked' is accepted as well as 'pending_payment', and
        // reports success. Cross-module commit ambiguity is unavoidable here
        // - the caller marks the hold paid and then confirms the booking on
        // a different DbContext, so a crash or a retried outbox message
        // replays this call against a hold that is already sold. Rejecting
        // that would make the retry that is supposed to finish the job the
        // thing that permanently fails it.
        //
        // COALESCE keeps the original booked_at across those replays: it
        // records when the range was sold, and a retry an hour later is not
        // a second sale.
        //
        // 'held' is still refused, and that distinction is the whole value
        // of the return: a hold released or expired out from under a
        // late-landing payment is inventory this platform no longer owns,
        // and the caller has to compensate rather than report success.
        const string sql = $"""
                            UPDATE unit_availability_holds
                            SET status = '{HoldStatuses.Booked}',
                                booked_at = COALESCE(booked_at, @Now),
                                client_key = NULL
                            WHERE id = @HoldId
                              AND status IN ('{HoldStatuses.PendingPayment}', '{HoldStatuses.Booked}');
                            """;

        try
        {
            int rowsAffected = await connection.ExecuteAsync(new CommandDefinition(
                sql, new { HoldId = holdId, Now = timeProvider.GetUtcNow() }, cancellationToken: cancellationToken));

            return rowsAffected > 0;
        }
        finally
        {
            if (openedHere)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    public async Task ReleaseHoldAsync(Guid holdId, CancellationToken cancellationToken)
    {
        // Same open/close symmetry as ConfirmHoldAsync above, and this is the
        // one of the two most likely to be called from outside a request:
        // ReconcileOrphanedBookedHoldsJob releases orphans from a background
        // scope, where an unreturned connection would be held for the length
        // of the run rather than of a request.
        DbConnection connection = dbContext.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
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
        //
        // Matches both post-checkout states, not just 'booked'. Every caller
        // of this is a compensation - ConfirmBookingHandler's two catch
        // blocks and its promo-rejection branch, ReconcileOrphanedBooking-
        // IntentsJob, CancelBookingHandler's outbox message, and the unpaid-
        // booking expiry job - and almost all of them now act on a hold that
        // is 'pending_payment', because that is what confirming produces.
        // Left matching 'booked' alone, every one of those would have become
        // a silent zero-row no-op and stranded the hold: the exact bug the
        // reconcile job exists to prevent, reintroduced in a WHERE clause
        // with no test failing to say so.
        const string sql = $"""
                            UPDATE unit_availability_holds
                            SET status = '{HoldStatuses.Held}', hold_expires_at = @Now, booked_at = NULL
                            WHERE id = @HoldId AND status IN ('{HoldStatuses.PendingPayment}', '{HoldStatuses.Booked}');
                            """;

        try
        {
            await connection.ExecuteAsync(new CommandDefinition(
                sql, new { HoldId = holdId, Now = timeProvider.GetUtcNow() }, cancellationToken: cancellationToken));
        }
        finally
        {
            if (openedHere)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }
}
