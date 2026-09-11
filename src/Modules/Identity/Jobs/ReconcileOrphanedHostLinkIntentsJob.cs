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
    ///     in between would strand the orphan again, which is the exact
    ///     failure this job exists to remove.
    ///     <para>
    ///         Precisely: <b>the claim, the unlink and the intent delete commit
    ///         together; DeleteAsync does not.</b> The first three are all
    ///         writes on this same AppIdentityDbContext inside one transaction,
    ///         which is what makes half-recovery impossible - a user cannot end
    ///         up unlinked with the intent still present, or the reverse.
    ///         The host deletion is no longer one of them, and no longer
    ///         races them. <c>IHostRegistrar.DeleteAsync</c> writes
    ///         AppHostsDbContext on its own connection, so calling it inline
    ///         committed the deletion before this transaction authorised it -
    ///         and the defence that "the next run repeats an idempotent
    ///         delete" only holds while nothing else can succeed in between.
    ///         The original become-host request can: the rollback restores its
    ///         intent and its user link, it resumes, and it completes against
    ///         a Host that has already been deleted.
    ///     </para>
    ///     <para>
    ///         It is a <c>DeleteHostOutboxMessage</c> now, committed with the
    ///         unlink and the intent delete and dispatched afterwards, with
    ///         the relay as the backstop the "next run" was standing in for.
    ///         See docs/adr/0025.
    ///     </para>
    ///     <para>
    ///         <c>DeleteAsync</c> can also run more than once for a single
    ///         logical reconciliation without any rollback at all: the
    ///         execution strategy re-runs this whole delegate on a transient
    ///         failure, and a failure at commit re-runs it after the delete has
    ///         already landed. Same requirement as the outbox dispatcher's
    ///         handlers - the cross-module call has to be idempotent, and this
    ///         one is by no-opping on a Host that no longer exists.
    ///     </para>
    ///     <para>
    ///         Like the outbox dispatcher and its Bookings twin, this holds a
    ///         row lock across a cross-module round trip. Deliberate rather
    ///         than accidental: acceptable because it is one row at a time
    ///         under a per-run cap, and SKIP LOCKED means a concurrent run
    ///         steps over a locked row instead of blocking behind it.
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

            // Unlink first, in this same transaction as the intent delete.
            // The intent now spans the whole BecomeHost operation rather than
            // just its first half, so a surviving one can mean the user was
            // already linked - a crash after the HostId write but before the
            // role was added. Deleting only the Host there would leave them
            // pointing at a row that no longer exists, which is the lockout
            // this job exists to prevent rather than cause.
            //
            // Guarded on the id: if the user became a host some other way
            // since, this intent is not about that link and must not clear it.
            ApplicationUser? user = await dbContext.Users
                .SingleOrDefaultAsync(u => u.Id == intent.UserId, cancellationToken);

            if (user is not null && user.HostId == intent.Id)
            {
                user.HostId = null;
            }

            // Intent.Id IS the host id - that is why this needs no
            // cross-module lookup to find what to clean up.
            //
            // Enqueued rather than called. DeleteAsync writes
            // AppHostsDbContext on its own connection, so calling it here
            // committed the deletion before the transaction that authorises
            // it. The old note defended that ordering as the lesser evil -
            // "if this succeeds and the commit then fails, the next run
            // repeats an idempotent delete" - and the repeat does converge,
            // but only if nothing else succeeds in between. The original
            // become-host request can: it resumes after the rollback restores
            // its intent and its user link, and completes against a Host row
            // that has already been deleted, leaving an account linked to
            // nothing.
            //
            // A durable row committed with the unlink and the intent delete
            // removes the window entirely, and the relay is the backstop the
            // "next run" was standing in for.
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
