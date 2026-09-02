using System.Diagnostics.Metrics;
namespace Outbox;

/// <summary>
///     Mirrors CommandTelemetry's shape - one shared Meter, registered once
///     with the OTel SDK, picked up automatically by every module's
///     dispatcher. The counters below are what's alertable in production; the
///     logs OutboxDispatcherBase emits alongside them are their
///     human-readable companions.
/// </summary>
public static class OutboxTelemetry
{
    public const string MeterName = "StayStack.Outbox";

    private static readonly Meter Meter = new Meter(MeterName);

    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "outbox.messages.dead_lettered",
        description: "Number of outbox messages that exhausted retries, tagged by module and message type.");

    /// <summary>
    ///     Distinct from DeadLettered, which only counts a row's *first*
    ///     crossing into dead-letter state. SweepDeadLetteredAsync retries a
    ///     dead-lettered row hourly forever, and docs/adr/0017 makes that
    ///     forever-retry load-bearing (it's why the reconcile job no longer
    ///     covers a hold behind a Cancelled booking). Without this counter, a
    ///     message whose failure is permanent loops indefinitely emitting
    ///     nothing at all - the retry is invisible precisely because Attempts
    ///     is already past MaxAttempts.
    /// </summary>
    public static readonly Counter<long> DeadLetterRetried = Meter.CreateCounter<long>(
        "outbox.messages.dead_letter_retried",
        description: "Number of dead-letter sweep retries that failed again, tagged by module and message type.");

    /// <summary>
    ///     Alertable in the other direction from the two above: a purge count
    ///     that drops to zero while bookings keep being made means retention
    ///     stopped running, and these tables only ever grow. A count pinned at
    ///     the per-run cap means the backlog is draining slower than it
    ///     accumulates.
    /// </summary>
    public static readonly Counter<long> Purged = Meter.CreateCounter<long>(
        "outbox.messages.purged",
        description: "Number of processed outbox messages deleted by retention, tagged by module.");
}
