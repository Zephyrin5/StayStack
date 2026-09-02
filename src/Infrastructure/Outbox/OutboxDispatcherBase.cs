using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
namespace Outbox;

/// <summary>
///     One per module, each over that module's own DbContext - so
///     Enqueue-then-SaveChangesAsync is atomic with the domain write it
///     accompanies. See docs/adr/0003.
///     <para>
///         No reflection: TryHandleAsync is a plain virtual switch over
///         OutboxMessage.Type; payload (de)serialization goes through the
///         caller's own source-generated JsonSerializerContext.
///     </para>
///     <para>
///         <b>Delivery is at-least-once, and handlers MUST be idempotent.</b>
///         This is a hard requirement of the contract, not a property of the
///         handlers that happen to exist today - see TryHandleAsync for the
///         three distinct ways one message reaches a handler more than once.
///         An earlier version of this comment said the handlers "happen to
///         be idempotent, but that's not something this should keep relying
///         on", which read as if the row lock below were on its way to
///         removing the requirement. It cannot: the lock excludes concurrent
///         dispatchers, which is a different guarantee from delivering once.
///     </para>
///     <para>
///         Every dispatch claims its row via `SELECT ... FOR UPDATE SKIP
///         LOCKED` (Postgres; see ClaimAndDispatchAsync for the Sqlite
///         fallback tests run under), inside a transaction scoped to that one
///         row. SKIP LOCKED means a second claimant simply doesn't see a
///         locked row, rather than blocking and duplicating work.
///     </para>
///     <para>
///         The handler runs INSIDE that transaction, so handler duration is
///         lock duration - and since handlers here call into other modules'
///         Contracts, which open their own DbContext against the same
///         database, a dispatch holds two pooled connections and leaves one
///         transaction idle-in-transaction for the length of the other's
///         work. That is a deliberate trade, not an oversight: keeping the
///         side effect inside the claim is what makes "one dispatcher at a
///         time per row" true, all the way through the side effect rather
///         than only up to the moment of claiming.
///     </para>
///     <para>
///         The alternative is a lease: claim, commit, run the handler
///         unlocked, then commit the outcome separately. It shortens the
///         transaction but does not remove the idempotency requirement (a
///         crash mid-handler still re-delivers), and it weakens exclusion -
///         a handler outliving its lease runs concurrently with the next
///         claimant, which is strictly worse for anything not idempotent.
///         Worth revisiting if a handler ever does unbounded work (a network
///         call, a large batch); today they are all in-process calls whose
///         duration is bounded by their own database work.
///     </para>
/// </summary>
public abstract partial class OutboxDispatcherBase<TDbContext>(
    TDbContext dbContext,
    TimeProvider timeProvider,
    ILogger logger) where TDbContext : DbContext
{
    // Capped, not uncapped - nothing here is latency-sensitive, but
    // retrying a poisoned message forever wastes cycles. 10 attempts at the
    // schedule below is roughly a day before it needs a human.
    private const int MaxAttempts = 10;

    // Indexed by (Attempts - 1); the last entry repeats past the table's
    // length rather than growing unbounded.
    private static readonly TimeSpan[] BackoffSteps =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1)
    ];

    // Resolved once, lazily - EF's own model metadata, not user input, so
    // safe to interpolate into raw SQL. Table names are module-prefixed
    // (every module shares one physical Postgres schema), which is why
    // this resolves dynamically instead of being hardcoded.
    private string? _claimByIdSql;

    private string ClaimByIdSql => _claimByIdSql ??= BuildClaimByIdSql();

    protected TDbContext DbContext { get; } = dbContext;

    protected abstract string ModuleName { get; }

    /// <summary>
    ///     Adds the row to the change tracker - the caller still has to call
    ///     SaveChangesAsync (ideally alongside the domain write this message
    ///     follows from, for atomicity). Returns the row so the caller can
    ///     dispatch it inline once that save commits.
    /// </summary>
    public OutboxMessage Enqueue<TMessage>(TMessage message, JsonTypeInfo<TMessage> typeInfo)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        OutboxMessage row = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = typeof(TMessage).Name,
            Payload = JsonSerializer.Serialize(message, typeInfo),
            CreatedAt = now,
            NextAttemptAt = now
        };

        DbContext.Set<OutboxMessage>().Add(row);
        return row;
    }

    /// <summary>
    ///     Dispatches one row and persists its outcome immediately - called
    ///     inline right after its enqueueing save commits (matching a direct
    ///     call's latency), and by DispatchPendingAsync for whatever an
    ///     earlier inline attempt didn't finish. Always re-claims by id (see
    ///     the class doc comment); `message` may be a tracked reference the
    ///     caller already holds, but locking requires a real query
    ///     regardless. No-op if the row is already locked, resolved, or
    ///     dead-lettered (those are only retried via SweepDeadLetteredAsync).
    /// </summary>
    public Task TryDispatchAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        ClaimAndDispatchAsync(message.Id, retryingDeadLetter: false, cancellationToken);

    /// <summary>
    ///     The relay job's poll: loads whatever's due (excluding
    ///     dead-lettered and still-backing-off rows) and dispatches it. Only
    ///     a candidate scan, not a claim - ordinary read-committed
    ///     visibility is fine here since the actual claim happens per-row in
    ///     TryDispatchAsync.
    /// </summary>
    public async Task DispatchPendingAsync(int batchSize, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        List<OutboxMessage> pending = await DbContext.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt == null && m.DeadLetteredAt == null && m.NextAttemptAt <= now)
            .OrderBy(m => m.CreatedAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (OutboxMessage message in pending)
        {
            await TryDispatchAsync(message, cancellationToken);
        }
    }

    /// <summary>
    ///     A dead letter means "stop retrying every poll," not "never
    ///     again" - standard DLQ practice, replaying each row's original
    ///     Payload rather than reconstructing the outcome from current state
    ///     (for a money-touching message that would mean re-deriving a
    ///     refund amount instead of replaying what's already recorded).
    ///     Cadence/cooldown are the caller's job (see each module's own
    ///     OutboxRelayJob); this only picks candidates. ClaimAndDispatchAsync
    ///     does the actual per-row claim and clears DeadLetteredAt
    ///     atomically with the rest of the outcome.
    /// </summary>
    public async Task SweepDeadLetteredAsync(int batchSize, TimeSpan cooldown, CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = timeProvider.GetUtcNow() - cooldown;

        List<OutboxMessage> deadLettered = await DbContext.Set<OutboxMessage>()
            .Where(m => m.ProcessedAt == null && m.DeadLetteredAt != null && m.DeadLetteredAt <= cutoff)
            .OrderBy(m => m.DeadLetteredAt)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        foreach (OutboxMessage message in deadLettered)
        {
            await ClaimAndDispatchAsync(message.Id, retryingDeadLetter: true, cancellationToken);
        }
    }

    /// <summary>
    ///     The actual claim-and-process step every dispatch path funnels
    ///     through. One transaction per row, not per batch - a batch-wide
    ///     transaction would hold every row's lock for the whole batch and
    ///     roll back already-succeeded dispatches (whose real side effects,
    ///     e.g. an actual ReleaseHoldAsync call, can't be undone by rolling
    ///     back this table's own row) if a later row failed.
    ///     <para>
    ///         retryingDeadLetter separates normal claims from sweep
    ///         retries - a normal claim must skip an actually dead-lettered
    ///         row (or it'd fight the sweep's cooldown), a retry must only
    ///         touch one still dead-lettered (it may have resolved
    ///         concurrently since the scan). DeadLetteredAt clears inside
    ///         the lock, not by the scan that found the row, so a second
    ///         concurrent scan can't see it as clear and race a normal
    ///         dispatch against this retry.
    ///     </para>
    /// </summary>
    private async Task ClaimAndDispatchAsync(Guid id, bool retryingDeadLetter, CancellationToken cancellationToken)
    {
        // Wrapped in the execution strategy, not a bare BeginTransactionAsync -
        // Npgsql's retry-on-failure is enabled, and a resilient strategy
        // throws on a manually-opened transaction it didn't create itself.
        // This re-runs the whole delegate, including the claim query and
        // TryHandleAsync, on a transient failure - safe because every
        // transactional write here must already be idempotent (ADR-0003),
        // and ChangeTracker.Clear() below guarantees each retry re-reads
        // fresh instead of stale in-memory state.
        //
        // OutboxTelemetry.DeadLettered.Add and the dead-letter log
        // deliberately stay OUTSIDE this delegate - they're process-local,
        // not part of the DB transaction, so a retried attempt would
        // double-fire them for one logical dispatch. Deferred until after
        // ExecuteAsync returns, so they fire only once per committed
        // outcome. OnDeadLetteredAsync stays inside: its own writes need to
        // commit atomically with the row, and it's required to be
        // idempotent the same way TryHandleAsync is.
        IExecutionStrategy strategy = DbContext.Database.CreateExecutionStrategy();

        (OutboxMessage? claimed, bool justDeadLettered, bool retryFailedAgain) = await strategy.ExecuteAsync(async () =>
        {
            // Retries reuse this DbContext - a rolled-back transaction
            // doesn't undo EF's tracking, so without this, a retry's claim
            // query returns the same entity a prior failed attempt already
            // mutated (e.g. ProcessedAt set) via identity resolution,
            // misreading it as claimed elsewhere and never actually
            // processing the row.
            DbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction = await DbContext.Database.BeginTransactionAsync(cancellationToken);

            // FOR UPDATE SKIP LOCKED is Postgres syntax - Sqlite (what unit
            // tests run the surrounding handlers against, since those
            // exercise pricing/compensation logic unrelated to this
            // locking) doesn't parse it. The plain fallback still re-reads
            // and re-validates the row, it just can't provide SKIP LOCKED's
            // cross-process exclusion - proven separately by
            // OutboxDispatcherConcurrencyTests against real Postgres.
            bool supportsSkipLocked = DbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

            OutboxMessage? candidate = supportsSkipLocked
                ? await DbContext.Set<OutboxMessage>().FromSqlRaw(ClaimByIdSql, id).SingleOrDefaultAsync(cancellationToken)
                : await DbContext.Set<OutboxMessage>().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

            if (candidate is null || candidate.ProcessedAt is not null || (candidate.DeadLetteredAt is not null) != retryingDeadLetter)
            {
                // Either SKIP LOCKED skipped an already-locked row, it's
                // already resolved, or its dead-lettered state doesn't
                // match what this caller is here to do. No lock was
                // meaningfully taken - the transaction rolls back on
                // dispose.
                return ((OutboxMessage?)null, false, false);
            }

            if (retryingDeadLetter)
            {
                candidate.DeadLetteredAt = null;
            }

            bool succeeded;

            try
            {
                await TryHandleAsync(candidate, cancellationToken);
                succeeded = true;
            }
            catch (Exception ex)
            {
                succeeded = false;
                candidate.LastError = ex.Message;
            }

            DateTimeOffset now = timeProvider.GetUtcNow();
            bool becameDeadLettered = false;

            // A sweep retry that failed again. Tracked separately because
            // becameDeadLettered is false here by construction (Attempts is
            // already past MaxAttempts), so without this the retry emits
            // nothing at all - see OutboxTelemetry.DeadLetterRetried.
            bool deadLetterRetryFailed = retryingDeadLetter && !succeeded;

            if (succeeded)
            {
                candidate.ProcessedAt = now;
            }
            else
            {
                // Captured before the increment - SweepDeadLetteredAsync
                // clears DeadLetteredAt before retrying, so a stuck message
                // re-crosses MaxAttempts every sweep. Attempts is never
                // reset, so without this the telemetry/hook below would
                // fire every retry, not just the first time.
                bool wasAlreadyDeadLettered = candidate.Attempts >= MaxAttempts;
                candidate.Attempts++;

                if (candidate.Attempts >= MaxAttempts)
                {
                    candidate.DeadLetteredAt = now;
                    becameDeadLettered = !wasAlreadyDeadLettered;

                    if (becameDeadLettered)
                    {
                        // Idempotent and transactional - safe inside the
                        // retried delegate, unlike the telemetry/log
                        // deferred below.
                        await OnDeadLetteredAsync(candidate, cancellationToken);
                    }
                }
                else
                {
                    TimeSpan backoff = BackoffSteps[Math.Min(candidate.Attempts - 1, BackoffSteps.Length - 1)];
                    candidate.NextAttemptAt = now + backoff;
                }
            }

            // Safe to re-run even if this fails mid-way (e.g. the process
            // dies between TryHandleAsync succeeding and this commit) - the
            // underlying action is already required to be idempotent
            // (ADR-0003), so a later re-dispatch of the same row is a
            // harmless repeat. An unhandled failure here rolls the
            // transaction back, leaving the row exactly as it was.
            await DbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return (candidate, becameDeadLettered, deadLetterRetryFailed);
        });

        if (claimed is null)
        {
            return;
        }

        // Only reached after the transaction above actually commits - see the
        // execution-strategy comment above for why these can't live inside the
        // retried delegate.
        if (justDeadLettered)
        {
            OutboxTelemetry.DeadLettered.Add(
                1,
                new KeyValuePair<string, object?>("module", ModuleName),
                new KeyValuePair<string, object?>("type", claimed.Type));
            LogDeadLettered(logger, ModuleName, claimed.Type, claimed.Id, claimed.Attempts, claimed.LastError);
        }
        else if (retryFailedAgain)
        {
            // Warning, not Error - the first crossing already logged at Error
            // and is the actionable event. This is the ongoing signal that
            // the message is still stuck, which is otherwise invisible.
            OutboxTelemetry.DeadLetterRetried.Add(
                1,
                new KeyValuePair<string, object?>("module", ModuleName),
                new KeyValuePair<string, object?>("type", claimed.Type));
            LogDeadLetterRetryFailed(logger, ModuleName, claimed.Type, claimed.Id, claimed.Attempts, claimed.LastError);
        }
    }

    private string BuildClaimByIdSql()
    {
        IEntityType entityType = DbContext.Model.FindEntityType(typeof(OutboxMessage))
                                  ?? throw new InvalidOperationException(
                                      $"{nameof(OutboxMessage)} has no mapped entity type on {typeof(TDbContext).Name}.");

        string table = entityType.GetTableName()
                        ?? throw new InvalidOperationException($"{nameof(OutboxMessage)} has no mapped table name.");
        string? schema = entityType.GetSchema();
        string qualifiedTable = schema is null ? $"\"{table}\"" : $"\"{schema}\".\"{table}\"";

        return $"SELECT * FROM {qualifiedTable} WHERE id = {{0}} FOR UPDATE SKIP LOCKED";
    }

    /// <summary>
    ///     Module-specific: switch over message.Type, deserialize via the
    ///     module's own JsonSerializerContext, call the matching Contracts
    ///     method(s). Returning normally means done - there is no
    ///     "return false to retry" case; let any genuine failure throw, and
    ///     ClaimAndDispatchAsync's own catch records it as LastError and
    ///     schedules a retry.
    ///     <para>
    ///         <b>This method MUST be idempotent</b> (ADR-0003). Not "should
    ///         be", and not "is, because today's handlers happen to be" - the
    ///         dispatcher cannot deliver exactly once, and there are three
    ///         separate ways it re-runs a handler for one logical message:
    ///     </para>
    ///     <list type="number">
    ///         <item>
    ///             The execution strategy re-runs the whole delegate on a
    ///             transient failure. A failure at COMMIT is the interesting
    ///             one: the handler already ran and its side effect already
    ///             happened, outside this database's control, and the retry
    ///             runs it again.
    ///         </item>
    ///         <item>
    ///             The process can die between the handler returning and the
    ///             commit that records it. The row stays unprocessed and a
    ///             later poll re-dispatches it. Same for any exception thrown
    ///             after the handler succeeded.
    ///         </item>
    ///         <item>
    ///             SweepDeadLetteredAsync deliberately replays a message's
    ///             original payload after it has already been attempted
    ///             MaxAttempts times - some of which may have had partial
    ///             effect.
    ///         </item>
    ///     </list>
    ///     <para>
    ///         In practice that means guarding on state rather than assuming
    ///         it: ReleaseHoldAsync updates WHERE status = 'booked',
    ///         ReverseRedemptionAsync WHERE reversed_at IS NULL. A handler
    ///         that blindly applies a delta - decrementing a counter, issuing
    ///         a refund without checking whether one was already issued - is
    ///         a money bug waiting for the first transient commit failure.
    ///         OutboxIdempotencyTests demonstrates the re-run happening.
    ///     </para>
    /// </summary>
    protected abstract Task TryHandleAsync(OutboxMessage message, CancellationToken cancellationToken);

    /// <summary>
    ///     No-op by default. A module overrides this only when a specific
    ///     dead-lettered message type warrants more than the log line and
    ///     counter above - e.g. flagging the row it was trying to act on for
    ///     manual review, so it surfaces in tooling that already queries
    ///     that module rather than requiring outbox internals knowledge.
    /// </summary>
    protected virtual Task OnDeadLetteredAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    // Past tense, deliberately - not a claim about current row state. A
    // module's OnDeadLetteredAsync override (see TransactionsOutboxDispatcher)
    // can resolve the row further in the same commit, e.g. setting
    // ProcessedAt and clearing DeadLetteredAt again - "is dead-lettered"
    // would then contradict the row by the time anyone reads this next to
    // the table; "was dead-lettered" just records that this attempt crossed
    // the retry threshold.
    [LoggerMessage(LogLevel.Error,
        "Outbox message {MessageId} ({MessageType}, module {Module}) was dead-lettered after {Attempts} attempts. Last error: {LastError}")]
    private static partial void LogDeadLettered(ILogger logger, string module, string messageType, Guid messageId, int attempts, string? lastError);

    [LoggerMessage(LogLevel.Warning,
        "Dead-lettered outbox message {MessageId} ({MessageType}, module {Module}) failed again on its sweep retry ({Attempts} attempts so far). Last error: {LastError}")]
    private static partial void LogDeadLetterRetryFailed(ILogger logger, string module, string messageType, Guid messageId, int attempts, string? lastError);
}
