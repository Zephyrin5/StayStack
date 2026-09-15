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
using BuildingBlocks.Persistence;
using Transactions.Contracts;
using TickerQ.Utilities.Base;
using TickerQ.Utilities.Models;
namespace Bookings.Jobs;

/// <summary>
///     Releases the inventory behind bookings nobody paid for.
///     <para>
///         A confirmed checkout moves its hold to 'pending_payment', which blocks
///         the range through the exclusion constraint. <see cref="Booking.PaymentDueAt"/>
///         makes that claim finite and this job enforces it: past the deadline, the
///         booking is cancelled and its hold released. Without it, holding a unit
///         and submitting checkout repeatedly would block inventory permanently
///         with no account and no payment (docs/adr/0020).
///     </para>
///     <para>
///         In Bookings because the cancellation and the release must move
///         together: a released hold under a live booking, or a cancelled booking
///         still blocking its range, are both wrong.
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
    // Bounds one run's work; an overdue booking stays overdue and is found again
    // next run.
    private const int MaxResultsPerRun = 1000;

    // Every minute: the payment window is tens of minutes, and a slower sweep adds
    // directly to how long an abandoned checkout keeps a unit off the market. The
    // scan is an indexed range read over Pending rows.
    [TickerFunction(functionName: "Bookings.ExpireUnpaidBookings", cronExpression: "* * * * *")]
    public async Task ExpireAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

    // Pending only. Confirmed and Cancelled bookings also have a null
    // PaymentDueAt; the status check does not rely on that.
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
            // Per booking, so one failing row does not strand every unit behind
            // it. Cancellation is not swallowed.
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
    ///     One transaction per booking: the re-read, the hold release and the
    ///     cancellation commit together, because ReleaseHoldAsync joins this
    ///     transaction (HoldConfirmation.AmbientTransaction). The redemption
    ///     reversal crosses into Promotions, so it is an outbox row saved with the
    ///     cancellation and dispatched after the commit (docs/adr/0003).
    /// </summary>
    private async Task ClaimAndExpireAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        bool expired = await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // The row is claimed by a bare SELECT ... FOR UPDATE, then loaded through
            // EF. A FromSqlRaw over `SELECT *` fails for Booking: EF composes a
            // projection asking for "TotalPrice_Amount", a column the snake_case
            // convention never produces for the complex Money property.
            bool supportsSkipLocked = dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

            if (supportsSkipLocked)
            {
                DbConnection connection = dbContext.Database.GetDbConnection();

                // BookingPaymentLock first, then the row - the order every path
                // taking both follows (docs/adr/0028). Every path that cancels a
                // booking takes it, because initiation never touches the booking row
                // and cannot see a cancellation that holds only the row lock.
                //
                // TryAcquireExclusiveSql is non-blocking by design: a contended
                // booking is skipped and found again next run. The blocking form
                // would put every booking in the batch behind one contended row.
                // Taken inside this transaction, so a skip below releases it with
                // the rollback.
                bool paymentLockTaken = await connection.ExecuteScalarAsync<bool>(new CommandDefinition(
                    AdvisoryLock.TryAcquireExclusiveSql,
                    new { LockKey = BookingPaymentLock.KeyFor(bookingId) },
                    transaction.GetDbTransaction(),
                    cancellationToken: cancellationToken));

                if (!paymentLockTaken)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return false;
                }

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

            // Re-checked under the lock rather than trusted from the scan: a payment
            // can confirm the booking in between, and expiring it would take a paid
            // stay away and hand the range to someone else.
            if (booking.BookingStatus != BookingStatus.Pending
                || booking.PaymentDueAt is null
                || booking.PaymentDueAt > timeProvider.GetUtcNow())
            {
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            // A succeeded payment whose confirmation has not reached Bookings yet.
            // Bookings learns of payment through an outbox message, so a paid booking
            // can look Pending and overdue until that message is delivered;
            // expiring it would cancel the stay and refund a payment that arrived on
            // time.
            //
            // Read last, immediately before the commit, so no payment that committed
            // Succeeded before this line can be expired. A payment succeeding between
            // this read and the commit remains possible - BookingPaymentLock excludes
            // opening a payment, not an existing one succeeding - and is refunded in
            // full through the obligation written below.
            if (await transactionLookup.HasSucceededPaymentAsync(booking.Id, cancellationToken))
            {
                LogPaidButUnconfirmed(logger, booking.Id);
                await transaction.RollbackAsync(cancellationToken);
                return false;
            }

            // A no-op unless the hold is 'pending_payment' or 'booked'.
            await holdConfirmation.ReleaseHoldAsync(booking.HoldId, cancellationToken);

            // The promo code goes back with the room: a redemption consumed by a
            // booking that never completes would burn a single-use code.
            OutboxMessage reverseRedemptionRow = dispatcher.Enqueue(
                new ReverseRedemptionOutboxMessage(booking.Id),
                BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage);

            DateTimeOffset cancelledAt = timeProvider.GetUtcNow();
            booking.Cancel(cancelledAt);

            // A refund obligation on every expiry, so the payment that can succeed
            // between the guard above and this commit has a refund path
            // (docs/adr/0027). On the ordinary path there is no payment and the
            // resolver records nothing.
            //
            // The full price, not guest policy: the guest did not cancel; the
            // platform reclaimed the inventory at its own deadline.
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

            // After the commit: dispatched inside, a reversal followed by a rollback
            // would leave a Promotions write with no Bookings state behind it.
            await dispatcher.TryDispatchAsync(reverseRedemptionRow, cancellationToken);

            return true;
        });

        if (expired)
        {
            // After the strategy returns, so a retried delegate counts one expiry
            // once.
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
