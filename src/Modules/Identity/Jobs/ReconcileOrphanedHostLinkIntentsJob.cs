using Hosts.Contracts;
using Identity.Entities;
using Outbox;
using Identity.Serialization;
using Identity.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Base;
namespace Identity.Jobs;

/// <summary>
///     Recovers a BecomeHostHandler run that died between RegisterHostAsync's
///     commit (Hosts' own database) and the Identity-side link - the one
///     window where an ordinary exception never got the chance to compensate,
///     so nothing was ever written to the outbox to retry. The Identity
///     counterpart of ReconcileOrphanedBookingIntentsJob; see docs/adr/0017.
///     <para>
///         BecomeHost was the last cross-module write in the codebase without
///         this cover. Its failed-update branches already compensate through
///         the outbox, but a hard process death between the two wrote nothing
///         anywhere - no intent, no outbox row, no job - and the orphaned Host
///         was permanent, with nothing pointing at it.
///     </para>
///     <para>
///         <b>What a surviving intent means.</b> BecomeHostHandler stages the
///         intent delete before AddToRoleAsync, which flushes it in that
///         call's own save, so the marker covers the whole operation:
///         register the Host, link it, add the role. A surviving intent
///         therefore means the operation did not finish, and the user may or
///         may not already be linked - which is why this undoes both halves
///         rather than only deleting the Host.
///     </para>
///     <para>
///         The two Identity-side writes - clearing HostId and deleting the
///         intent - commit together, so this cannot half-recover. The grace
///         period is not what makes that safe; it only decides how long a
///         partial BecomeHost lingers before collection.
///     </para>
/// </summary>
public partial class ReconcileOrphanedHostLinkIntentsJob(
    AppIdentityDbContext dbContext,
    IdentityOutboxDispatcher dispatcher,
    TimeProvider timeProvider,
    ILogger<ReconcileOrphanedHostLinkIntentsJob> logger)
{
    // Caps one run's work. No lookback bound to pair it with - an intent older
    // than any window is still found next run, so hitting the cap delays
    // recovery rather than forfeiting it.
    private const int MaxResultsPerRun = 1000;

    [TickerFunction(functionName: "Identity.ReconcileOrphanedHostLinkIntents", cronExpression: "*/5 * * * *")]
    public async Task ReconcileAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = timeProvider.GetUtcNow() - PendingHostLinkIntent.ReconcileGrace;

        List<Guid> candidateIds = await dbContext.PendingHostLinkIntents.AsNoTracking()
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
            // Per item, so one bad row does not end the batch. Nothing here is
            // classified transient, so EnableRetryOnFailure does not absorb it:
            // a DbUpdateConcurrencyException - the realistic one, from a row
            // changing under this claim - would propagate straight out of
            // ReconcileAsync and abandon every candidate after it.
            //
            // Throughput rather than correctness, since the survivors are found
            // again on the next run. But the next run is five minutes later and
            // hits the same row first, so a single persistently-conflicting
            // intent could starve everything behind it indefinitely.
            //
            // Cancellation is deliberately NOT swallowed: on shutdown this
            // should stop, not log a failure per remaining candidate.
            try
            {
                await ClaimAndReconcileAsync(intentId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                IdentityTelemetry.OrphanedHostLinkIntentReconcileFailed.Add(1);
                LogReconcileFailed(logger, intentId, ex);
            }
        }
    }

    /// <summary>
    ///     One transaction per row, mirroring
    ///     ReconcileOrphanedBookingIntentsJob and OutboxDispatcherBase - the
    ///     claim must not commit ahead of the work it authorises, or a death
    ///     in between would strand the orphan.
    ///     <para>
    ///         <b>The claim, the unlink, the intent delete and the
    ///         DeleteHostOutboxMessage commit together.</b> All are writes on this
    ///         AppIdentityDbContext in one transaction, so a user cannot end up
    ///         unlinked with the intent still present, or the reverse.
    ///     </para>
    ///     <para>
    ///         The host deletion is an outbox message, not an inline
    ///         <c>IHostRegistrar.DeleteAsync</c>: that writes AppHostsDbContext on
    ///         its own connection and would commit before this transaction
    ///         authorises it. If this transaction then rolled back, the original
    ///         become-host request could resume with its intent and user link
    ///         restored and complete against a deleted Host. The relay is the
    ///         backstop (docs/adr/0025).
    ///     </para>
    ///     <para>
    ///         The dispatched delete can still run more than once, so it no-ops on
    ///         a Host that no longer exists - the same idempotency every outbox
    ///         handler needs.
    ///     </para>
    ///     <para>
    ///         Holds a row lock for one row at a time under a per-run cap, and
    ///         SKIP LOCKED means a concurrent run steps over a locked row instead
    ///         of blocking behind it.
    ///     </para>
    /// </summary>
    private async Task ClaimAndReconcileAsync(Guid intentId, CancellationToken cancellationToken)
    {
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        OutboxMessage? deleteHostRow = await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // FOR UPDATE SKIP LOCKED is Postgres syntax; the fallback still
            // re-reads and re-validates, it just can't provide cross-process
            // exclusion - same split, and same reasoning, as the sibling job.
            bool supportsSkipLocked = dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

            PendingHostLinkIntent? intent = supportsSkipLocked
                ? await dbContext.PendingHostLinkIntents
                    .FromSqlRaw("""SELECT * FROM "pending_host_link_intents" WHERE id = {0} FOR UPDATE SKIP LOCKED""", intentId)
                    .SingleOrDefaultAsync(cancellationToken)
                : await dbContext.PendingHostLinkIntents.SingleOrDefaultAsync(i => i.Id == intentId, cancellationToken);

            if (intent is null)
            {
                // Locked by a concurrent run, or the request that owns it
                // finished between the scan above and this claim.
                return null;
            }

            // Unlink first, in this same transaction as the intent delete. The
            // intent spans the whole BecomeHost operation, so a surviving one can
            // mean the user was already linked - a crash after the HostId write
            // but before the role was added. Deleting only the Host would leave
            // them pointing at a row that no longer exists.
            //
            // Guarded on the id: if the user became a host some other way
            // since, this intent is not about that link and must not clear it.
            ApplicationUser? user = await dbContext.Users
                .SingleOrDefaultAsync(u => u.Id == intent.UserId, cancellationToken);

            if (user is not null && user.HostId == intent.Id)
            {
                user.HostId = null;
            }

            // Intent.Id is the host id, so no cross-module lookup is needed.
            // Enqueued rather than called - see ClaimAndReconcileAsync.
            OutboxMessage deleteHostRow = dispatcher.Enqueue(
                new DeleteHostOutboxMessage(intent.Id),
                IdentityJsonSerializerContext.Default.DeleteHostOutboxMessage);

            dbContext.PendingHostLinkIntents.Remove(intent);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return deleteHostRow;
        });

        if (deleteHostRow is not null)
        {
            // After the commit, never inside it - a deletion that landed and
            // was then rolled back is the stranded cross-module write this
            // rewrite exists to prevent.
            await dispatcher.TryDispatchAsync(deleteHostRow, cancellationToken);

            // Deferred until after the commit - a process-local side effect,
            // so firing it inside the retried delegate would double-count one
            // logical reconciliation.
            IdentityTelemetry.OrphanedHostLinkIntentReconciled.Add(1);
            LogReconciled(logger, intentId);
        }
    }

    [LoggerMessage(LogLevel.Error,
        "Failed to reconcile orphaned intent {IntentId}; the batch continued and the next run will retry it. A row failing every run is stuck and needs a look")]
    private static partial void LogReconcileFailed(ILogger logger, Guid intentId, Exception exception);

    [LoggerMessage(LogLevel.Warning,
        "ReconcileOrphanedHostLinkIntents hit its per-run cap of {MaxResultsPerRun} candidates - orphans may be arriving faster than this job clears them")]
    private static partial void LogResultsCapped(ILogger logger, int maxResultsPerRun);

    [LoggerMessage(LogLevel.Warning,
        "Reconciled orphaned host-link intent {IntentId} - the Host registered under that id was deleted. This means a BecomeHost died mid-flight; a spike here is worth investigating")]
    private static partial void LogReconciled(ILogger logger, Guid intentId);
}
