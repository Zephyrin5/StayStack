using Hosts.Contracts;
using Identity.Entities;
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
///         <b>Why this cannot delete a live Host.</b> BecomeHostHandler marks
///         the intent for deletion before calling UserManager.UpdateAsync,
///         and UserManager resolves the same scoped AppIdentityDbContext, so
///         the intent delete and the ApplicationUser.HostId write commit in
///         one SaveChanges. A user who is linked therefore has no intent, and
///         an intent that survives means the link never committed. The grace
///         period is not what makes that safe - it only decides how long an
///         orphan lingers before collection.
///     </para>
/// </summary>
public partial class ReconcileOrphanedHostLinkIntentsJob(
    AppIdentityDbContext dbContext,
    IHostRegistrar hostRegistrar,
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
            await ClaimAndReconcileAsync(intentId, cancellationToken);
        }
    }

    /// <summary>
    ///     One transaction per row, mirroring
    ///     ReconcileOrphanedBookingIntentsJob and OutboxDispatcherBase - the
    ///     claim must not commit ahead of the work it authorises, or a death
    ///     in between would strand the orphan again, which is the exact
    ///     failure this job exists to remove.
    ///     <para>
    ///         Precisely: <b>the claim and the intent delete commit together;
    ///         DeleteAsync runs on AppHostsDbContext and may repeat.</b> That
    ///         is safe because it no-ops on a Host that is already gone, so a
    ///         rollback anywhere just means the next run repeats it.
    ///     </para>
    /// </summary>
    private async Task ClaimAndReconcileAsync(Guid intentId, CancellationToken cancellationToken)
    {
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        bool reconciled = await strategy.ExecuteAsync(async () =>
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
                return false;
            }

            // Intent.Id IS the host id - that is why this needs no
            // cross-module lookup to find what to clean up.
            await hostRegistrar.DeleteAsync(intent.Id, cancellationToken);

            dbContext.PendingHostLinkIntents.Remove(intent);
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return true;
        });

        if (reconciled)
        {
            // Deferred until after the commit - a process-local side effect,
            // so firing it inside the retried delegate would double-count one
            // logical reconciliation.
            IdentityTelemetry.OrphanedHostLinkIntentReconciled.Add(1);
            LogReconciled(logger, intentId);
        }
    }

    [LoggerMessage(LogLevel.Warning,
        "ReconcileOrphanedHostLinkIntents hit its per-run cap of {MaxResultsPerRun} candidates - orphans may be arriving faster than this job clears them")]
    private static partial void LogResultsCapped(ILogger logger, int maxResultsPerRun);

    [LoggerMessage(LogLevel.Warning,
        "Reconciled orphaned host-link intent {IntentId} - the Host registered under that id was deleted. This means a BecomeHost died mid-flight; a spike here is worth investigating")]
    private static partial void LogReconciled(ILogger logger, Guid intentId);
}
