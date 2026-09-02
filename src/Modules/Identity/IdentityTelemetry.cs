using System.Diagnostics.Metrics;
namespace Identity;

/// <summary>
///     Mirrors CommandTelemetry/OutboxTelemetry/BookingsTelemetry's shape -
///     one Meter, registered once with the OTel SDK, picked up automatically.
/// </summary>
public static class IdentityTelemetry
{
    public const string MeterName = "StayStack.Identity";

    private static readonly Meter Meter = new Meter(MeterName);

    /// <summary>
    ///     Incremented once per intent ReconcileOrphanedHostLinkIntentsJob
    ///     actually reconciles. Each one means a BecomeHost died between
    ///     Hosts' registration commit and the Identity-side link - rare by
    ///     construction, so a sustained rate is a real signal rather than
    ///     background noise, and worth an alert threshold.
    /// </summary>
    public static readonly Counter<long> OrphanedHostLinkIntentReconciled = Meter.CreateCounter<long>(
        "identity.orphaned_host_link_intents.reconciled",
        description: "Number of orphaned host-link intents whose registered Host was deleted.");
}
