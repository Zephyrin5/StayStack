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
    ///         ambient EF transaction: not handed it explicitly, a command on an
    ///         enlisted connection either fails or commits on its own while the
    ///         caller believes it is inside their transaction.
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
    ///         of a decision whose other half is a Bookings row: the transition
    ///         and the booking, the payment and the confirmation, the release and
    ///         the cancellation. The two halves must commit together.
    ///     </para>
    ///     <para>
    ///         Required so that "participates in your transaction" versus "commits
    ///         immediately" is not decided silently by whoever opened a
    ///         transaction several frames up; a caller without one finds out here
    ///         rather than through a hold released beneath a cancellation that
    ///         rolled back.
    ///     </para>
    ///     <para>
    ///         A throw rather than opening one: a transaction opened on the
    ///         caller's behalf would make each statement atomic with nothing but
    ///         itself.
    ///     </para>
    /// </summary>
    private DbTransaction RequiredTransaction([System.Runtime.CompilerServices.CallerMemberName] string? caller = null) =>
        AmbientTransaction ?? throw new InvalidOperationException(
            $"{nameof(HoldConfirmation)}.{caller} must be called inside a transaction. It changes a hold's status, and " +
            "that transition is always half of a decision whose other half is a Bookings row - committing it on its own " +
            "is how a hold ends up released beneath a cancellation that never landed.");

    // Raw shape of the RETURNING row, materialized first and assembled into
    // Money afterward (docs/adr/0006, applied to Dapper). The three amounts
    // share one currency column, so they cannot be mapped straight onto Money.
    // CurrencyTypeHandler converts the column to the enum.
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
        // The status/expiry check in the WHERE clause makes this succeed at most
        // once per hold: a second call finds no 'held' row. @Now (the app's
        // TimeProvider), not Postgres' now(), so one clock compares the expiry.
        //
        // 'pending_payment', not 'booked': submitting a checkout form is not
        // paying, and 'booked' is inventory nothing reclaims. booked_at stays
        // null for the same reason. MarkHoldPaidAsync is the only writer of
        // 'booked'.
        //
        // client_key is kept: the concurrent-hold cap counts 'held' and
        // 'pending_payment' together, since a transition out of the cap's sight
        // would escape it. MarkHoldPaidAsync clears it (docs/adr/0016).
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
            // onto its three amounts; callers receive Money.
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

        // The only writer of 'booked', driven from the payment-confirmation path
        // (MarkTransactionSucceededHandler -> IBookingPaymentConfirmation).
        // booked_at records when the range was sold, which is now.
        //
        // client_key is cleared: nothing reads a network address once the row
        // is past the payment window, and a booked row outlives the hold by
        // years. Nothing restores it - ReleaseHoldAsync resets hold_expires_at
        // to now, putting the row outside the cap's WHERE clause regardless.
        //
        // Idempotent: 'booked' is accepted as well as 'pending_payment' and
        // reports success, so a repeated call against a hold already sold does
        // not fail the payment. COALESCE keeps the original booked_at across
        // repeats.
        //
        // 'held' is refused, and that is the value of the return: a hold
        // released or expired under a late-landing payment is inventory this
        // platform no longer owns, and the caller must compensate.
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
        // Same open/close symmetry as ConfirmHoldAsync above. This is the one
        // most often called from a background scope - ExpireUnpaidBookingsJob
        // releases holds - where an unreturned connection would be held for the
        // whole run.
        DbConnection connection = dbContext.Database.GetDbConnection();
        bool openedHere = connection.State != ConnectionState.Open;
        if (openedHere)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        // hold_expires_at reset to now, so a released range stops blocking new
        // holds at once instead of for whatever was left on the original timer,
        // and the row is immediately eligible for cleanup.
        //
        // Matches both post-checkout states. Every caller is a cancellation
        // (CancelBookingHandler, the expiry job), and most act on a
        // 'pending_payment' hold; matching 'booked' alone would make each a
        // silent zero-row no-op that strands the hold. 'held' is excluded: no booking stands behind a 'held' hold, so no
        // caller has a claim on one.
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
