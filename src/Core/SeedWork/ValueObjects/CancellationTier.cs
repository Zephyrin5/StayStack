namespace SeedWork.ValueObjects;

// One rung of a CancellationPolicy's tier ladder - "refund RefundPercent if
// the cancellation happens at least MinDaysBeforeCheckIn days before
// check-in". Per-tier shape validation (range checks) lives here; the
// cross-tier invariants (a zero-floor tier must exist, thresholds distinct,
// percent non-increasing toward check-in) belong to the owning
// CancellationPolicy, since they're only meaningful across the whole list.
public sealed record CancellationTier(int MinDaysBeforeCheckIn, decimal RefundPercent);
