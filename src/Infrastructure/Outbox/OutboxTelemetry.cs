using System.Diagnostics.Metrics;
namespace Outbox;

/// <summary>
///     Mirrors BuildingBlocks.Observability.CommandTelemetry's shape - one
///     shared Meter, registered once with the OTel SDK, picked up
///     automatically by every module's dispatcher rather than each module
///     wiring its own. The counter below is what's actually alertable in
///     production; the Error-level log OutboxDispatcherBase also emits on
///     dead-letter is the human-readable companion, not a substitute.
/// </summary>
public static class OutboxTelemetry
{
    public const string MeterName = "StayStack.Outbox";

    private static readonly Meter Meter = new Meter(MeterName);

    public static readonly Counter<long> DeadLettered = Meter.CreateCounter<long>(
        "outbox.messages.dead_lettered",
        description: "Number of outbox messages that exhausted retries, tagged by module and message type.");
}
