using SeedWork.ValueObjects;
namespace Bookings.Features.CancelBooking;

/// <summary>The guest-policy refund a cancellation owes, before any payment is considered (docs/adr/0027).</summary>
public static class CancellationRefund
{
    /// <param name="today">The property-local date (docs/adr/0018).</param>
    public static Money Compute(Money total, CancellationPolicy policy, DateOnly checkIn, DateOnly today)
    {
        int daysBeforeCheckIn = Math.Max(checkIn.DayNumber - today.DayNumber, 0);

        // Divide first in plain decimal: Money rounds on every operation.
        return total * (policy.ResolveRefundPercent(daysBeforeCheckIn) / 100m);
    }
}
