using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
namespace Outbox;

/// <summary>
///     One per module (BookingsOutboxDispatcher, TransactionsOutboxDispatcher,
///     IdentityOutboxDispatcher), each constructed over that module's own
///     DbContext so Enqueue/TryDispatchAsync/DispatchPendingAsync all read
///     and write through the same scoped instance a handler's own writes go
///     through - that's what makes Enqueue-then-SaveChangesAsync atomic with
///     whatever domain write it accompanies. See docs/adr/0003.
///     <para>
///         No reflection anywhere in the dispatch path: TryHandleAsync is a
///         plain virtual method each module overrides with a hand-written
///         switch over OutboxMessage.Type, and payload (de)serialization
///         always goes through the caller's own source-generated
///         JsonSerializerContext.
///     </para>
///     <para>
///         Every dispatch attempt - inline from a handler, DispatchPendingAsync's
///         batch, SweepDeadLetteredAsync's retry - claims its row via
///         `SELECT ... FOR UPDATE SKIP LOCKED` (against Postgres; see
///         ClaimAndDispatchAsync for the Sqlite fallback the unit test
///         suite runs under) inside a short-lived transaction held only for
///         that one row's processing, not a plain read. Without this, two
///         overlapping runs of the same relay job (one slower than the
///         one-minute cron, or two app instances) can both select and
///         dispatch the same row: nothing today would stop it structurally,
///         only every current message type happening to be idempotent
///         papers over it - load-bearing behavior nothing enforces for
///         whatever message type gets added next. SKIP LOCKED means a
///         second concurrent claim attempt on a row already locked by the
///         first simply doesn't see it (returns no rows), rather than
///         blocking and duplicating work once the first claimant releases
///         it.
///     </para>
/// </summary>
public abstract partial class OutboxDispatcherBase<TDbContext>(
    TDbContext dbContext,
    TimeProvider timeProvider,
    ILogger logger) where TDbContext : DbContext
{
    // Retried forever would waste cycles on a genuinely poisoned message;
    // uncapped is unnecessary when nothing here is latency-sensitive (every
    // message type is a best-effort follow-up, never blocking a response) -
    // 10 attempts at the backoff schedule below is roughly a day of retrying
    // before something needs a human.
    private const int MaxAttempts = 10;

    // Indexed by (Attempts - 1); the last entry repeats once Attempts
    // exceeds the table's length rather than growing without bound.
    private static readonly TimeSpan[] BackoffSteps =
    [
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(15),
        TimeSpan.FromHours(1)
    ];

    // Resolved once per dispatcher instance, not per call - EF's own model
    // metadata, not user input, so safe to interpolate directly into raw
    // SQL. Table names are module-prefixed (bookings_outbox_messages etc,
    // see AppBookingsDbContext.BookingsOutboxMessages) precisely because
    // every module shares one physical Postgres schema - resolving this
    // dynamically rather than hardcoding it here is what lets one base
    // class serve all three modules' differently-named tables.
    private string? _claimByIdSql;

    private string ClaimByIdSql => _claimByIdSql ??= BuildClaimByIdSql();

    protected TDbContext DbContext { get; } = dbContext;

    protected abstract string ModuleName { get; }

    /// <summary>
    ///     Adds the row to DbContext's change tracker - the caller still has
    ///     to call DbContext.SaveChangesAsync (ideally in the same call that
    ///     saves whatever domain write this message follows from, for the
    ///     atomicity guarantee this whole mechanism exists for). Returns the
    ///     row so the caller can dispatch it inline once that save commits.
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
    ///     Dispatches one specific row and immediately persists its outcome.
    ///     Called inline right after a message's own enqueueing save commits
    ///     (keeps happy-path latency identical to a direct call), and by
    ///     DispatchPendingAsync for whatever the inline attempt didn't
    ///     finish. Claims the row under FOR UPDATE SKIP LOCKED first (see
    ///     the class doc comment) - `message` may already be a tracked
    ///     reference the caller holds (e.g. straight from Enqueue), but
    ///     locking can only happen via a real query, so this always re-claims
    ///     by id regardless. A no-op if the row is already locked by another
    ///     concurrent claim, already resolved, or already dead-lettered
    ///     (dead-lettered rows are only ever retried through
    ///     SweepDeadLetteredAsync, never through this path).
    /// </summary>
    public Task TryDispatchAsync(OutboxMessage message, CancellationToken cancellationToken) =>
        ClaimAndDispatchAsync(message.Id, retryingDeadLetter: false, cancellationToken);

    /// <summary>
    ///     What the relay job's poll calls - loads whatever's due and
    ///     dispatches it. Excludes dead-lettered rows and anything still
    ///     backing off (NextAttemptAt in the future). This is only a
    ///     candidate scan, not a claim - id order isn't disturbed by another
    ///     process's row locks, so ordinary read-committed visibility is
    ///     fine here; the actual claim happens per-row in TryDispatchAsync.
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
    ///     A dead letter is "stop retrying every poll interval," not "never
    ///     try again" - standard dead-letter-queue practice (Kafka/RabbitMQ/
    ///     SQS shops all replay their DLQs) applied here: give dead-lettered
    ///     rows one more attempt per sweep, using the row's own original
    ///     Payload rather than trying to reconstruct the right outcome from
    ///     current state elsewhere (which for a money-touching message would
    ///     mean re-deriving the exact refund amount CancelBookingHandler
    ///     computed once, a strictly worse and more error-prone answer than
    ///     just replaying what's already durably recorded). Cadence and
    ///     cooldown are the caller's job (see Bookings/Transactions/Identity's
    ///     own OutboxRelayJob) - this only answers "which rows are due," not
    ///     "how often to ask." Like DispatchPendingAsync, this is only a
    ///     candidate scan; ClaimAndDispatchAsync does the actual per-row
    ///     claim and is what clears DeadLetteredAt, atomically with the rest
    ///     of the dispatch outcome, not this scan.
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
    ///     through. One transaction per row, held only for as long as that
    ///     row's own processing takes - not one transaction for a whole
    ///     batch, which would hold many rows' locks for the batch's entire
    ///     wall-clock time and roll back already-succeeded dispatches (whose
    ///     real side effects, e.g. an actual ReleaseHoldAsync call elsewhere,
    ///     already happened and can't be undone by rolling back this table's
    ///     own row) if a later row in the same batch failed unexpectedly.
    ///     <para>
    ///         retryingDeadLetter distinguishes DispatchPendingAsync's normal
    ///         claims from SweepDeadLetteredAsync's retries: a normal claim
    ///         must not touch a row that's actually dead-lettered (or the
    ///         fast retry loop would fight the sweep's own cooldown), and a
    ///         retry must only touch a row that's still actually
    ///         dead-lettered (it may have already been resolved - e.g. by a
    ///         concurrent retry - between the caller's batch scan and this
    ///         claim). DeadLetteredAt is cleared here, inside the lock,
    ///         rather than by the scan that found the row - clearing it
    ///         before the claim would let a second concurrent scan see the
    ///         row as no-longer-dead-lettered and race a normal dispatch
    ///         attempt against this same retry.
    ///     </para>
    /// </summary>
    private async Task ClaimAndDispatchAsync(Guid id, bool retryingDeadLetter, CancellationToken cancellationToken)
    {
        // Wrapped in the configured execution strategy, not a bare
        // BeginTransactionAsync - ConfigureStayStackDefaults enables
        // Npgsql's retry-on-failure (transient errors like deadlocks/
        // serialization failures get retried), and a resilient execution
        // strategy refuses a manually-opened transaction it didn't create
        // itself (EF Core throws "does not support user-initiated
        // transactions" otherwise) since it can't safely retry only part of
        // one. CreateExecutionStrategy().ExecuteAsync re-runs this entire
        // delegate - including the claim query and TryHandleAsync - from
        // the start on a transient failure, which is safe precisely because
        // every *transactional* write here is already required to be
        // idempotent (ADR-0003) for the ordinary relay-retry case anyway,
        // and each retry re-reads `claimed` fresh (the previous attempt's
        // in-memory changes were never committed).
        //
        // The one thing that ISN'T safe to leave inside this delegate: a
        // non-transactional side effect. OutboxTelemetry.DeadLettered.Add
        // and the dead-letter log line are process-local, not part of the
        // DB transaction - a rolled-back-and-retried attempt would fire
        // them again for what's really one logical dispatch, the same
        // "rate instead of a count" problem SweepDeadLetteredAsync's own
        // repeated retries have (see wasAlreadyDeadLettered below), just at
        // the execution-strategy layer instead of the sweep-loop layer.
        // Both are deferred until after ExecuteAsync actually returns -
        // guaranteed to only ever happen once per successful commit.
        // OnDeadLetteredAsync stays inside, deliberately: its own writes
        // (see TransactionsOutboxDispatcher's override) need to commit
        // atomically with the rest of this row's outcome, and it's already
        // required to be idempotent the same way TryHandleAsync is, so
        // re-running it across a transactional retry is safe by the same
        // reasoning as the rest of this delegate.
        IExecutionStrategy strategy = DbContext.Database.CreateExecutionStrategy();

        (OutboxMessage? claimed, bool justDeadLettered) = await strategy.ExecuteAsync(async () =>
        {
            // First line of every attempt, including retries - a rolled-back
            // DB transaction does not undo this DbContext's in-memory change
            // tracking, and EF's identity resolution means the query below
            // would otherwise hand back whatever entity a *previous*,
            // rolled-back attempt already mutated (e.g. still showing
            // ProcessedAt set) instead of the row's real, current database
            // state. Without this, a transient failure that happens after
            // `candidate` was mutated but before the commit lands makes the
            // very next attempt misread its own stale in-memory write as
            // "already processed by someone else" and bail out - the row
            // never actually gets marked processed, and the next relay poll
            // dispatches it again for real. Same pattern already used in
            // ConfirmBookingHandler/BecomeHostHandler's own compensating
            // paths.
            DbContext.ChangeTracker.Clear();

            await using IDbContextTransaction transaction = await DbContext.Database.BeginTransactionAsync(cancellationToken);

            // FOR UPDATE SKIP LOCKED is Postgres syntax - Sqlite (what the
            // unit test suite runs the surrounding handlers against, since
            // those tests exercise handler/pricing/compensation logic that
            // has nothing to do with this locking mechanism itself) doesn't
            // parse it. Real claim-locking only applies against the real
            // provider; the plain fallback still re-reads and re-validates
            // the row (nothing here skips the ProcessedAt/DeadLetteredAt
            // checks below), it just can't provide the cross-process mutual
            // exclusion SKIP LOCKED does. Proven against real Postgres by
            // OutboxDispatcherConcurrencyTests, not by anything running
            // under Sqlite.
            bool supportsSkipLocked = DbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL";

            OutboxMessage? candidate = supportsSkipLocked
                ? await DbContext.Set<OutboxMessage>().FromSqlRaw(ClaimByIdSql, id).SingleOrDefaultAsync(cancellationToken)
                : await DbContext.Set<OutboxMessage>().SingleOrDefaultAsync(m => m.Id == id, cancellationToken);

            if (candidate is null || candidate.ProcessedAt is not null || (candidate.DeadLetteredAt is not null) != retryingDeadLetter)
            {
                // Either SKIP LOCKED skipped it (another claim already
                // holds this row's lock right now), or it's already
                // resolved, or it's dead-lettered/not-dead-lettered in a
                // way that doesn't match what this caller is here to do.
                // No lock was meaningfully taken - the transaction below
                // just rolls back on dispose.
                return ((OutboxMessage?)null, false);
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

            if (succeeded)
            {
                candidate.ProcessedAt = now;
            }
            else
            {
                // Captured before the increment: SweepDeadLetteredAsync
                // clears DeadLetteredAt before retrying (see its own doc
                // comment), so a renewed failure here re-crosses the
                // MaxAttempts threshold every single sweep, forever, for a
                // message that's genuinely stuck - Attempts is never reset,
                // so without this check the telemetry/hook below would fire
                // on every hourly retry, not just the first time this row
                // actually became dead-lettered.
                bool wasAlreadyDeadLettered = candidate.Attempts >= MaxAttempts;
                candidate.Attempts++;

                if (candidate.Attempts >= MaxAttempts)
                {
                    candidate.DeadLetteredAt = now;
                    becameDeadLettered = !wasAlreadyDeadLettered;

                    if (becameDeadLettered)
                    {
                        // Idempotent and transactional (see this method's
                        // own doc comment for why it's safe to leave inside
                        // the retried delegate, unlike the telemetry/log
                        // deferred below).
                        await OnDeadLetteredAsync(candidate, cancellationToken);
                    }
                }
                else
                {
                    TimeSpan backoff = BackoffSteps[Math.Min(candidate.Attempts - 1, BackoffSteps.Length - 1)];
                    candidate.NextAttemptAt = now + backoff;
                }
            }

            // Idempotent to re-run even if this itself fails mid-way (e.g.
            // the process dies between TryHandleAsync succeeding and this
            // commit) - the underlying action is already documented
            // idempotent (ADR-0003), so a later claim re-dispatching this
            // same row is safe, just a harmless repeat. The transaction
            // rolling back on an unhandled failure here (a real
            // SaveChangesAsync/commit error, not TryHandleAsync's own
            // already-caught exceptions) leaves the row exactly as it was
            // before this attempt - Attempts not incremented, nothing
            // lost, just retried again later (or, under the execution
            // strategy, retried again immediately from the top of this
            // same delegate).
            await DbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return (candidate, becameDeadLettered);
        });

        if (claimed is not null && justDeadLettered)
        {
            // Only reached once the transaction above has actually
            // committed - see this method's own doc comment for why these
            // two specifically can't live inside the retried delegate.
            OutboxTelemetry.DeadLettered.Add(
                1,
                new KeyValuePair<string, object?>("module", ModuleName),
                new KeyValuePair<string, object?>("type", claimed.Type));
            LogDeadLettered(logger, ModuleName, claimed.Type, claimed.Id, claimed.Attempts, claimed.LastError);
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
    ///     method(s). Returning normally means done - the underlying action
    ///     is expected to be idempotent (ADR-0003), so there's no
    ///     "return false to retry" case; let any genuine failure throw and
    ///     TryDispatchAsync's own catch records it as LastError and retries.
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

    // Past tense, deliberately - not a claim about the row's current state.
    // A module's own OnDeadLetteredAsync override (see
    // TransactionsOutboxDispatcher's) can resolve the row further in the
    // same commit this log is reporting on - e.g. compensating and then
    // setting ProcessedAt/clearing DeadLetteredAt again, so it no longer
    // matches a `WHERE dead_lettered_at IS NOT NULL` query at all by the
    // time anyone reads this line next to the table. "is dead-lettered"
    // would flatly contradict what's actually in the row at that point;
    // "was dead-lettered" just records that this dispatch attempt crossed
    // the retry threshold, true regardless of what happened afterward.
    [LoggerMessage(LogLevel.Error,
        "Outbox message {MessageId} ({MessageType}, module {Module}) was dead-lettered after {Attempts} attempts. Last error: {LastError}")]
    private static partial void LogDeadLettered(ILogger logger, string module, string messageType, Guid messageId, int attempts, string? lastError);
}
