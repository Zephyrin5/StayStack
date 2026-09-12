using Bookings.Contracts;
using Bookings.Outbox;
using Bookings.Serialization;
using Outbox;
using Bookings.Entities;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using System.Data.Common;
using Microsoft.Extensions.Logging;
using Transactions.Contracts;
using TickerQ.Utilities.Base;
using TickerQ.Utilities.Models;
namespace Bookings.Jobs;

/// <summary>
///     Releases the inventory behind bookings nobody paid for.
///     <para>
///         Confirming a checkout takes a unit off the market: the hold moves
///         to 'pending_payment' and goes on blocking its range through the
///         exclusion constraint, which carries no status predicate. Nothing
///         reclaimed those rows. An anonymous caller could hold a unit,
///         submit the checkout form, and repeat - each submission converting
///         a 15-minute hold into a permanent one, escaping the per-client cap
///         (which counts only 'held') and never paying. Inventory denial with
///         no account, no payment, and nothing in the system able to tell
///         those rows from genuinely sold ones.
///     </para>
///     <para>
///         This is the other half of <see cref="Booking.PaymentDueAt"/>: the
///         deadline makes the claim finite, and this job is what enforces it.
///         Together with the hold endpoint's rate limit they bound the whole
///         abuse - a caller who can create R holds per window can hold at
///         most R × the payment window at once, and the excess expires
///         without anyone intervening.
///     </para>
///     <para>
///         Lives in Bookings, not Availability, because the two halves have
///         to move together. Releasing the hold alone would leave a guest
///         holding a booking with no inventory behind it; cancelling the
///         booking alone would leave the range blocked forever. Availability
///         cannot cancel a booking - it sits upstream of this module and has
///         no idea one exists (docs/adr/0004) - so the module that owns both
///         the deadline and the booking owns the sweep.
///     </para>
/// </summary>
public partial class ExpireUnpaidBookingsJob(
    AppBookingsDbContext dbContext,
    IHoldConfirmation holdConfirmation,
    ITransactionLookup transactionLookup,
    BookingsOutboxDispatcher dispatcher,
    TimeProvider timeProvider,
    ILogger<ExpireUnpaidBookingsJob> logger)
{
    // Same cap and same reasoning as ReconcileOrphanedBookingIntentsJob's:
    // bounds one run's work, and costs nothing in recovery since an overdue
    // booking stays overdue and is found again next run.
    private const int MaxResultsPerRun = 1000;

    // Every minute, not every five. The payment window is measured in tens of
    // minutes, so a five-minute sweep would add a fifth of that window to how
    // long an abandoned checkout keeps a unit off the market. The scan is an
    // indexed range read over a small set of Pending rows.
    [TickerFunction(functionName: "Bookings.ExpireUnpaidBookings", cronExpression: "* * * * *")]
    public async Task ExpireAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        // Pending only. A Confirmed booking has been paid for and its hold is
        // 'booked'; a Cancelled one has already given its hold back. Both
        // also have a null PaymentDueAt, so this predicate is belt and braces
        // on the status rather than relying on the field alone.
        List<Guid> candidateIds = await dbContext.Bookings.AsNoTracking()
            .Where(b => b.BookingStatus == BookingStatus.Pending
                        && b.PaymentDueAt != null
                        && b.PaymentDueAt <= now)
            .OrderBy(b => b.PaymentDueAt)
            .Take(MaxResultsPerRun)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        if (candidateIds.Count == 0)
        {
            return;
        }

        if (candidateIds.Count == MaxResultsPerRun)
        {
            LogResultsCapped(logger, MaxResultsPerRun);
        }

        foreach (Guid bookingId in candidateIds)
        {
            // Per booking, so one bad row doesn't end the batch and strand
            // every unit behind it - the same reasoning, and the same
            // deliberate non-swallowing of cancellation, as
            // ReconcileOrphanedBookingIntentsJob's loop.
            try
            {
                await ClaimAndExpireAsync(bookingId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                BookingsTelemetry.UnpaidBookingExpireFailed.Add(1);
                LogExpireFailed(logger, bookingId, ex);
            }
        }
    }

    /// <summary>
    ///     One transaction per booking, mirroring
    ///     ReconcileOrphanedBookingIntentsJob.ClaimAndReconcileAsync: the
    ///     cancellation and the hold release must not be able to commit
    ///     independently in the wrong order.
    ///     <para>
    ///         The re-read, the release and the cancellation now all commit
    ///         together. The hold is in this module's own DbContext, so
    ///         ReleaseHoldAsync joins this transaction (see
    ///         HoldConfirmation.AmbientTransaction) instead of autocommitting
    ///         beside it.
    ///     </para>
    ///     <para>
    ///         That retires an ordering argument this job used to carry. The
    ///         release had to precede the cancellation's commit, because the
    ///         two could commit independently and the wrong order would leave
    ///         a Cancelled booking whose hold nothing would ever release
    ///         again - the next run's Pending filter would no longer match it,
    ///         and the range stayed blocked permanently. There is no longer a
    ///         wrong order to pick: a rollback takes both.
    ///     </para>
    ///     <para>
    ///         ReverseRedemptionAsync is the one piece that cannot join,
    ///         since it writes AppPromotionsDbContext across a module
    ///         boundary. It goes on the outbox in the same SaveChangesAsync as
    ///         the cancellation and is dispatched inline afterwards, matching
    ///         CancelBookingHandler - so it is durable before anything acts on
    ///         it, and OutboxRelayJob delivers whatever this run misses
    ///         (docs/adr/0003).
    ///     </para>
    /// </summary>
    private async Task ClaimAndExpireAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        bool expired = await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // The lock is taken by a bare SELECT ... FOR UPDATE rather than
            // through FromSqlRaw, unlike ReconcileOrphanedBookingIntentsJob's
            // otherwise-identical claim. That job's entity is all scalars;
            // Booking carries Money as a complex property, and EF composes
            // its own projection over a raw query - asking for
            // "TotalPrice_Amount", which the snake_case naming convention
            // never produced. `SELECT *` returns the real columns and the
            // composed projection then fails on a column that does not
            // exist. Claiming the id alone sidesteps the projection entirely
            // and the row lock is held by the transaction just the same, so
            // the entity can be read back through EF normally.
            bool supportsSkipLocked = dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

            if (supportsSkipLocked)
            {
                DbConnection connection = dbContext.Database.GetDbConnection();

                Guid? claimedId = await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                    """SELECT id FROM "bookings" WHERE id = @BookingId FOR UPDATE SKIP LOCKED""",
                    new { BookingId = bookingId },
                    transaction.GetDbTransaction(),
                    cancellationToken: cancellationToken));

                if (claimedId is null)
                {
                    // Locked by a concurrent run, or gone.
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }
            }

            Booking? booking = await dbContext.Bookings.SingleOrDefaultAsync(b => b.Id == bookingId, cancellationToken);

            if (booking is null)
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            // Re-checked under the lock, not trusted from the scan above. A
            // payment can resolve between the two - the window closing is
            // exactly when a slow gateway is most likely to land - and
            // cancelling a booking that has just been paid for would take a
            // paid stay away from a guest and hand their range to someone
            // else.
            if (booking.BookingStatus != BookingStatus.Pending
                || booking.PaymentDueAt is null
                || booking.PaymentDueAt > timeProvider.GetUtcNow())
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            // The one question this module cannot answer from its own state,
            // and the only thing standing between a successful payment and
            // being expired out from under it.
            //
            // Everything above is Bookings-side, and Bookings learns about a
            // payment through an outbox message. MarkTransactionSucceededHandler
            // commits Succeeded and that message together and then dispatches
            // it inline as a best effort; when that best effort fails, the row
            // waits for OutboxRelayJob's cron. Through that whole window a paid
            // booking is Pending with a lapsed PaymentDueAt, which is precisely
            // what the scan and the re-check above look for. The re-check does
            // not help: it re-reads the same rows.
            //
            // What followed was not merely a lost booking. The expiry releases
            // the hold and cancels, the confirmation finally arrives,
            // ConfirmPaymentAsync sees Cancelled and returns false, and
            // TransactionsOutboxDispatcher refunds a payment that had
            // succeeded minutes earlier - a guest who paid on time losing both
            // the stay and, if the range was re-sold in between, any chance of
            // getting it back.
            //
            // Read last, immediately before the cancellation commits, so the
            // guarantee is as strong as one read can make it: no payment that
            // committed Succeeded before this line can be expired. What remains
            // is a genuine tie - a payment committing between this read and the
            // commit below, microseconds wide - and it is not closable from
            // here without one module locking the other's rows. At that point
            // the payment really did land as the claim lapsed, and the existing
            // refund is a defensible answer to it. That is a different thing
            // from refunding payments that beat the deadline by minutes, which
            // is what this closes.
            if (await transactionLookup.HasSucceededPaymentAsync(booking.Id, cancellationToken))
            {
                LogPaidButUnconfirmed(logger, booking.Id);
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            // Idempotent, and still worth being: a no-op unless the hold is
            // 'pending_payment' or 'booked', so a retried attempt costs
            // nothing.
            await holdConfirmation.ReleaseHoldAsync(booking.HoldId, cancellationToken);

            // The promo code goes back to the guest along with the room. A
            // redemption is consumed at checkout, so leaving it spent for a
            // booking that never happened would quietly burn a single-use
            // code - the same compensation the orphaned-intent job performs
            // for the same reason.
            OutboxMessage reverseRedemptionRow = dispatcher.Enqueue(
                new ReverseRedemptionOutboxMessage(booking.Id),
                BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage);

            DateTimeOffset cancelledAt = timeProvider.GetUtcNow();
            booking.Cancel(cancelledAt);

            // An obligation here too, and this is the gap that had no path at
            // all: this job enqueued a redemption reversal and nothing else, so
            // a payment committing between the guard above and this
            // cancellation produced a cancelled booking with a succeeded
            // payment and no reversal message of any kind - not even a losing
            // one. Nothing retried, because nothing had been written.
            //
            // The full amount, not a policy percentage. The guest did not ask
            // for this - the platform reclaimed the inventory at its own
            // deadline - so charging a cancellation fee would be billing them
            // for our clock. On the ordinary path there is no payment at all
            // and the resolver no-ops, which costs one row per expiry.
            dbContext.RefundObligations.Add(new RefundObligation
            {
                BookingId = booking.Id,
                CancelledAt = cancelledAt,
                PolicyRefundAmount = booking.TotalPrice.Amount,
                Currency = booking.TotalPrice.Currency,
                Cause = BookingCancellationCause.Expiry,
                NextAttemptAt = cancelledAt
            });
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            // Dispatched after the commit, not inside it. Inside, a
            // successful reversal followed by a rollback would strand a
            // Promotions write with no Bookings state explaining it.
            await dispatcher.TryDispatchAsync(reverseRedemptionRow, cancellationToken);

            return true;
        });

        if (expired)
        {
            // After the commit, for the same reason the reconcile job defers
            // its counter: a retried delegate would otherwise count one
            // logical expiry more than once.
            BookingsTelemetry.UnpaidBookingExpired.Add(1);
            LogExpired(logger, bookingId);
        }
    }

    [LoggerMessage(LogLevel.Information,
        "Expired unpaid booking {BookingId}; its hold was released and any promo redemption reversed")]
    private static partial void LogExpired(ILogger logger, Guid bookingId);

    [LoggerMessage(LogLevel.Warning,
        "Booking {BookingId} is past its payment deadline but has a succeeded payment, so it was left alone. Its confirmation is still on the outbox - if this repeats for the same booking, that message is stuck")]
    private static partial void LogPaidButUnconfirmed(ILogger logger, Guid bookingId);

    [LoggerMessage(LogLevel.Error,
        "Failed to expire unpaid booking {BookingId}; the batch continued and the next run will retry it. A row failing every run is holding inventory and needs a look")]
    private static partial void LogExpireFailed(ILogger logger, Guid bookingId, Exception exception);

    [LoggerMessage(LogLevel.Warning,
        "ExpireUnpaidBookings hit its per-run cap of {MaxResultsPerRun} bookings - unpaid bookings may be arriving faster than this job clears them")]
    private static partial void LogResultsCapped(ILogger logger, int maxResultsPerRun);
}
