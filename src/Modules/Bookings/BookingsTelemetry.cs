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
}
