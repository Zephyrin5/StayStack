using Bookings.Entities;
using BuildingBlocks.Exceptions;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Data;
using System.Data.Common;
namespace Bookings.Contracts;

// internal, same reasoning as Hosts.Contracts' implementations - Bookings
// should only ever reach this through IHoldConfirmation, resolved via DI.
internal class HoldConfirmation(AppBookingsDbContext dbContext, TimeProvider timeProvider) : IHoldConfirmation
{
    /// <summary>
    ///     The columns a <see cref="ConfirmedHold"/> is built from, shared by
    ///     the confirming UPDATE...RETURNING and the read below so a column
    ///     added to one cannot go missing from the other - they must agree,
    ///     since both map onto the same record by name.
    /// </summary>
    private const string HoldProjection =
        """
        unit_id AS "UnitId", lower(stay_range) AS "CheckIn", upper(stay_range) AS "CheckOut", guest_count AS "GuestCount", total_price AS "TotalPrice", subtotal AS "Subtotal", currency AS "Currency", length_of_stay_discount_amount AS "LengthOfStayDiscountAmount"
        """;

    /// <summary>
    ///     The caller's transaction, when there is one.
    ///     <para>
    ///         These statements are Dapper, and Dapper does not discover an
    ///         ambient EF transaction: without being handed it explicitly,
    ///         a command on an enlisted connection either fails or - worse -
    ///         commits on its own while the caller believes it is inside
    ///         their transaction. That autocommit was the whole shape of
    ///         defect 3, and now that holds live in this module it is
    ///         avoidable rather than inherent.
    ///     </para>
    ///     <para>
    ///         Null when the caller has no transaction open. Every read here
    ///         is fine with that. Every <em>write</em> is not, which is why
    ///         they go through <see cref="RequiredTransaction"/> instead.
    ///     </para>
    /// </summary>
    private DbTransaction? AmbientTransaction => dbContext.Database.CurrentTransaction?.GetDbTransaction();

    /// <summary>
    ///     The caller's transaction, required.
    ///     <para>
    ///         The three statements that change a hold's status are each half
    ///         of a decision whose other half lives in a Bookings row: the
    ///         transition and the intent, the payment and the confirmation,
    ///         the release and the cancellation. Every one of them has already
    ///         been the subject of a defect where the two halves committed
    ///         independently, and the whole point of holds living in this
    ///         module is that they no longer have to.
    ///     </para>
    ///     <para>
    ///         Without this, the difference between "participates in your
    ///         transaction" and "commits immediately, whatever you do next" is
    ///         invisible at the call site and decided by whoever opened a
    ///         transaction several frames up. Every caller today is correct;
    ///         the next one has nothing to tell it what it owes, and the way it
    ///         would find out is a hold released beneath a cancellation that
    ///         rolled back.
    ///     </para>
    ///     <para>
    ///         A throw rather than opening one here. Opening a transaction on
    ///         the caller's behalf would make each of these atomic with
    ///         nothing but itself, which is exactly the shape that reads as
    ///         safe and is not.
    ///     </para>
    /// </summary>
    private DbTransaction RequiredTransaction([System.Runtime.CompilerServices.CallerMemberName] string? caller = null) =>
        AmbientTransaction ?? throw new InvalidOperationException(
            $"{nameof(HoldConfirmation)}.{caller} must be called inside a transaction. It changes a hold's status, and " +
            "that transition is always half of a decision whose other half is a Bookings row - committing it on its own " +
            "is how a hold ends up released beneath a cancellation that never landed.");

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
        // Bookings' expiry job. The concurrent-hold cap does read it here -
        // it counts 'held' and 'pending_payment' together, because a
        // transition that moved a row out of the cap's sight was the way to
        // escape the cap entirely. See HoldAvailabilityHandler's count query.
        const string sql = $"""
                            UPDATE unit_availability_holds
                            SET status = '{HoldStatuses.PendingPayment}'
                            WHERE id = @HoldId AND status = '{HoldStatuses.Held}' AND hold_expires_at > @Now
                            RETURNING {HoldProjection};
                            """;

        ConfirmedHoldRow? row = await connection.QuerySingleOrDefaultAsync<ConfirmedHoldRow>(
            new CommandDefinition(sql, new { HoldId = holdId, Now = timeProvider.GetUtcNow() },
                RequiredTransaction(), cancellationToken: cancellationToken));

        if (row is null)
        {
            throw new NotFoundException("Hold", holdId);
        }

        return MapConfirmedHold(row);
    }

    private static ConfirmedHold MapConfirmedHold(ConfirmedHoldRow row)
    {
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

    public async Task<ConfirmedHold?> GetConfirmedHoldAsync(Guid holdId, CancellationToken cancellationToken)
    {
        DbConnection connection = dbContext.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        try
        {
            // Deliberately no expiry check, unlike ConfirmHoldAsync's WHERE.
            // A hold in 'pending_payment' has already been sold into a
            // checkout; hold_expires_at stopped governing it at that
            // transition, and the payment deadline on the Booking governs it
            // now. Reading one back is not re-confirming it.
            const string sql = $"""
                                SELECT {HoldProjection}
                                FROM unit_availability_holds
                                WHERE id = @HoldId AND status = '{HoldStatuses.PendingPayment}';
                                """;

            ConfirmedHoldRow? row = await connection.QuerySingleOrDefaultAsync<ConfirmedHoldRow>(
                new CommandDefinition(sql, new { HoldId = holdId },
                    AmbientTransaction, cancellationToken: cancellationToken));

            return row is null ? null : MapConfirmedHold(row);
        }
        finally
        {
            if (openedHere)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
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
                sql, new { HoldId = holdId, Now = timeProvider.GetUtcNow() },
                RequiredTransaction(), cancellationToken: cancellationToken));

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
                sql, new { HoldId = holdId, Now = timeProvider.GetUtcNow() },
                RequiredTransaction(), cancellationToken: cancellationToken));
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
