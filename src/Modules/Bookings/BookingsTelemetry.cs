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
    ///     Incremented once per booking ExpireUnpaidBookingsJob expires - a
    ///     checkout that was submitted and never paid for, whose unit has now
    ///     been returned to inventory.
    ///     <para>
    ///         Not expected to be near zero: abandoned checkouts are ordinary
    ///         customer behaviour.
    ///         What is worth alerting on is the shape - a sharp rise means
    ///         either payment is failing for real guests or someone is
    ///         cycling checkouts to hold inventory, and both want a look.
    ///     </para>
    /// </summary>
    public static readonly Counter<long> UnpaidBookingExpired = Meter.CreateCounter<long>(
        "bookings.unpaid_bookings.expired",
        description: "Number of unpaid bookings cancelled and whose held unit was released back to inventory.");

    /// <summary>
    ///     The counterpart to UnpaidBookingExpired, and the more urgent of the
    ///     two: a booking that fails to expire is holding a unit off the
    ///     market indefinitely, and the job will keep finding it first because
    ///     the scan is ordered by due date. Alert on this being non-zero and
    ///     sustained.
    /// </summary>
    public static readonly Counter<long> UnpaidBookingExpireFailed = Meter.CreateCounter<long>(
        "bookings.unpaid_bookings.expire_failed",
        description: "Number of unpaid bookings whose expiry threw and was skipped for this run.");
}
