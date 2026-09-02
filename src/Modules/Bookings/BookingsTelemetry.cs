using System.Diagnostics.Metrics;
namespace Bookings;

/// <summary>
///     Mirrors CommandTelemetry/OutboxTelemetry's shape - one Meter,
///     registered once with the OTel SDK, picked up automatically.
/// </summary>
public static class BookingsTelemetry
{
    public const string MeterName = "StayStack.Bookings";

    private static readonly Meter Meter = new Meter(MeterName);

    /// <summary>
    ///     Incremented once per intent ReconcileOrphanedBookingIntentsJob
    ///     actually reconciles. Each one means a confirmation died between
    ///     Availability's hold commit and the Booking insert - rare by
    ///     construction, so a sustained rate here is a real signal rather than
    ///     background noise, and worth an alert threshold.
    /// </summary>
    public static readonly Counter<long> OrphanedIntentReconciled = Meter.CreateCounter<long>(
        "bookings.orphaned_intents.reconciled",
        description: "Number of orphaned booking intents whose hold was released and redemption reversed.");

    /// <summary>
    ///     The counterpart to OrphanedIntentReconciled, and the reason it is not
    ///     enough on its own: a run where every row throws reports zero
    ///     reconciliations, which reads exactly like a run with nothing to do.
    ///     Alert on this being non-zero and sustained - a row that fails every
    ///     run is stuck, and the job will keep finding it first.
    /// </summary>
    public static readonly Counter<long> OrphanedIntentReconcileFailed = Meter.CreateCounter<long>(
        "bookings.orphaned_intents.reconcile_failed",
        description: "Number of booking intents whose reconciliation threw and was skipped for this run.");
}
