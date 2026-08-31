using System.Diagnostics.Metrics;
namespace Outbox;

/// <summary>
///     Mirrors CommandTelemetry's shape - one shared Meter, registered once
///     with the OTel SDK, picked up automatically by every module's
///     dispatcher. The counter below is what's alertable in production; the
///     Error-level log OutboxDispatcherBase emits on dead-letter is its
///     human-readable companion.
/// </summary>
public static class OutboxTelemetry
{
    public const string MeterName = "StayStack.Outbox";

    private static readonly Meter Meter = new Meter(MeterName);

    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "outbox.messages.dead_lettered",
        description: "Number of outbox messages that exhausted retries, tagged by module and message type.");
}
