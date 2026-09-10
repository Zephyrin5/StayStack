namespace Outbox;

/// <summary>
///     The batch sizes, retention windows and cadences every module's
///     OutboxRelayJob runs on.
///     <para>
///         Each module needs its own job class - TickerQ's
///         <c>[TickerFunction]</c> takes a compile-time literal name, and the
///         names have to differ per module - but nothing about the numbers
///         differs. They were declared identically in three places, which is
///         three chances for one to drift and no way to notice: an outbox
///         relaying at a different cadence in one module than another would
///         look deliberate at every individual call site.
///     </para>
///     <para>
///         The cron expressions are here for the same reason and are usable
///         in an attribute because they are <c>const</c> - a named constant
///         also says what the schedule is for, where a bare "0 * * * *" at
///         the attribute has to be decoded.
///     </para>
/// </summary>
public static class OutboxRelaySchedule
{
    /// <summary>
    ///     Rows dispatched per relay run, and per dead-letter sweep run.
    /// </summary>
    public const int BatchSize = 50;

    /// <summary>
    ///     How long a dead-lettered row waits before its next attempt. There
    ///     is no point retrying a genuinely poisoned message every minute at
    ///     the same cadence as ordinary pending rows.
    /// </summary>
    public static readonly TimeSpan DeadLetterSweepCooldown = TimeSpan.FromHours(1);

    /// <summary>
    ///     How long a processed row is kept before retention deletes it.
    ///     <para>
    ///         Not zero: a processed row is the only record that a
    ///         compensating action was dispatched at all, and these carry
    ///         money, so a window of them is what makes "did we actually
    ///         reverse that?" answerable afterwards. 30 days comfortably
    ///         outlives any support conversation about a booking.
    ///     </para>
    /// </summary>
    public static readonly TimeSpan ProcessedRetention = TimeSpan.FromDays(30);

    /// <summary>
    ///     Larger batches than the dispatch path, because purging is a plain
    ///     delete with no per-row side effect - but still capped per run, so
    ///     a long-neglected table drains over several nights instead of one
    ///     lock-holding sweep.
    /// </summary>
    public const int PurgeBatchSize = 1000;

    /// <inheritdoc cref="PurgeBatchSize"/>
    public const int PurgeMaxBatchesPerRun = 50;

    /// <summary>
    ///     Every minute - the finest granularity confirmed elsewhere in this
    ///     codebase's cron usage (5-field, no seconds field). The relay is
    ///     the primary delivery path whenever a handler's inline dispatch
    ///     attempt fails, not a rare-crash backstop, so it runs far more
    ///     often than the 5-minute reconciliation jobs.
    /// </summary>
    public const string RelayCron = "* * * * *";

    /// <summary>
    ///     Hourly. Gives dead-lettered rows another chance using their own
    ///     originally-computed Payload - see
    ///     <c>OutboxDispatcherBase.SweepDeadLetteredAsync</c> for why that is
    ///     preferred over a bespoke reconciliation job.
    /// </summary>
    public const string DeadLetterSweepCron = "0 * * * *";

    /// <summary>
    ///     Daily, off-peak. Nothing depends on processed rows being gone
    ///     promptly - the only cost of keeping them longer is table size - so
    ///     this runs at the coarsest cadence of the three.
    /// </summary>
    public const string PurgeProcessedCron = "30 3 * * *";
}
