using Availability.Contracts;
using Bookings.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Promotions.Contracts;
using TickerQ.Utilities.Base;
namespace Bookings.Jobs;

/// <summary>
///     Recovers a ConfirmBookingHandler run that died between
///     ConfirmHoldAsync's commit (Availability's own database) and the Booking
///     insert - the one window where an ordinary exception never got the
///     chance to compensate, so nothing was ever written to the outbox to
///     retry. See docs/adr/0017.
///     <para>
///         Driven entirely by Bookings' own pending_booking_intents table.
///         The jobs this replaces asked another module for candidates over a
///         rolling window and joined the answer against Bookings in
///         application memory; an intent row states the fact directly, so
///         there is no cross-module lookup, no window past which an orphan
///         becomes unreachable, and nothing to keep in sync.
///     </para>
///     <para>
///         Both compensations are safe to repeat: ReleaseHoldAsync only acts
///         on a hold still 'booked', ReverseRedemptionAsync only on a
///         redemption still active, and it no-ops entirely when this booking
///         never redeemed a code - which is why one job can cover both
///         without knowing whether a promotion was involved.
///     </para>
/// </summary>
public partial class ReconcileOrphanedBookingIntentsJob(
    AppBookingsDbContext dbContext,
    IHoldConfirmation holdConfirmation,
    IPromotionRedemption promotionRedemption,
    TimeProvider timeProvider,
    ILogger<ReconcileOrphanedBookingIntentsJob> logger)
{
    // Caps the work one run does, in case an unexpected volume of orphans
    // ever appears at once. Unlike the jobs this replaces there's no lookback
    // bound to pair it with - an intent older than any window is still found
    // on the next run, so hitting the cap delays recovery rather than
    // forfeiting it.
    private const int MaxResultsPerRun = 1000;

    [TickerFunction(functionName: "Bookings.ReconcileOrphanedBookingIntents", cronExpression: "*/5 * * * *")]
    public async Task ReconcileAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = timeProvider.GetUtcNow() - PendingBookingIntent.ReconcileGrace;

        List<Guid> candidateIds = await dbContext.PendingBookingIntents.AsNoTracking()
            .Where(i => i.CreatedAt <= cutoff)
            .OrderBy(i => i.CreatedAt)
            .Take(MaxResultsPerRun)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        if (candidateIds.Count == 0)
        {
            return;
        }

        if (candidateIds.Count == MaxResultsPerRun)
        {
            LogResultsCapped(logger, MaxResultsPerRun);
        }

        foreach (Guid intentId in candidateIds)
        {
            await ClaimAndReconcileAsync(intentId, cancellationToken);
        }
    }

    /// <summary>
    ///     One transaction per row, mirroring OutboxDispatcherBase.
    ///     ClaimAndDispatchAsync - the claim must not commit ahead of the work
    ///     it authorises. Resolving the intent first (an autocommitting
    ///     UPDATE ... RETURNING, say) would strand the hold forever if the
    ///     process died in between, which is the exact failure this job
    ///     exists to remove.
    ///     <para>
    ///         Precisely: <b>the claim and the delete commit together; the
    ///         cross-module work is idempotent and may repeat.</b> It is not
    ///         all three atomically - ReleaseHoldAsync runs on
    ///         AppAvailabilityDbContext and ReverseRedemptionAsync on
    ///         AppPromotionsDbContext, separate connections committing
    ///         independently. A rollback anywhere just means the next run
    ///         repeats both calls, which their contracts allow.
    ///     </para>
    ///     <para>
    ///         Like the outbox dispatcher, this holds a row lock across
    ///         cross-module round trips. Deliberate rather than accidental:
    ///         acceptable because it is one row at a time with a per-run cap,
    ///         and SKIP LOCKED means a concurrent run steps over a locked row
    ///         instead of blocking behind it.
    ///     </para>
    /// </summary>
    private async Task ClaimAndReconcileAsync(Guid intentId, CancellationToken cancellationToken)
    {
        // Wrapped in the execution strategy, not a bare BeginTransactionAsync -
        // Npgsql's retry-on-failure is enabled and a resilient strategy throws
        // on a manually-opened transaction it didn't create. Re-running the
        // whole delegate is safe for the same reason a repeat run is.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        bool reconciled = await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // FOR UPDATE SKIP LOCKED is Postgres syntax; the fallback still
            // re-reads and re-validates, it just can't provide cross-process
            // exclusion - same split, and same reasoning, as
            // OutboxDispatcherBase's own claim query.
            bool supportsSkipLocked = dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

            PendingBookingIntent? intent = supportsSkipLocked
                ? await dbContext.PendingBookingIntents
                    .FromSqlRaw("""SELECT * FROM "pending_booking_intents" WHERE id = {0} FOR UPDATE SKIP LOCKED""", intentId)
                    .SingleOrDefaultAsync(cancellationToken)
                : await dbContext.PendingBookingIntents.SingleOrDefaultAsync(i => i.Id == intentId, cancellationToken);

            if (intent is null)
            {
                // Locked by a concurrent run, or the request that owns it
                // finished between the scan above and this claim.
                return false;
            }

            await holdConfirmation.ReleaseHoldAsync(intent.HoldId, cancellationToken);
            await promotionRedemption.ReverseRedemptionAsync(intent.Id, cancellationToken);

            dbContext.PendingBookingIntents.Remove(intent);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return true;
        });

        if (reconciled)
        {
            // Deferred until after the commit - a process-local side effect,
            // not part of the transaction, so firing it inside the retried
            // delegate would double-count one logical reconciliation. Same
            // reasoning as OutboxTelemetry's counters.
            BookingsTelemetry.OrphanedIntentReconciled.Add(1);
            LogReconciled(logger, intentId);
        }
    }

    [LoggerMessage(LogLevel.Warning,
        "ReconcileOrphanedBookingIntents hit its per-run cap of {MaxResultsPerRun} candidates - orphans may be arriving faster than this job clears them")]
    private static partial void LogResultsCapped(ILogger logger, int maxResultsPerRun);

    [LoggerMessage(LogLevel.Warning,
        "Reconciled orphaned booking intent {IntentId} - its hold was released and any promotion redemption reversed. This means a confirmation died mid-flight; a spike here is worth investigating")]
    private static partial void LogReconciled(ILogger logger, Guid intentId);
}
