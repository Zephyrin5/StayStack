namespace Bookings.Contracts;

/// <summary>
///     How a refund obligation ended. Set with <c>ResolvedAt</c>, and only then: an unresolved
///     obligation has no outcome, because the payment it is waiting for has not landed yet.
/// </summary>
public enum RefundObligationOutcome
{
    /// <summary>
    ///     Nothing was owed. No payment succeeded against the booking and none can any more - once the
    ///     cancellation commits no new payment starts, so an obligation with no open payment behind it
    ///     is finished rather than pending.
    /// </summary>
    NothingOwed = 0,

    /// <summary>A refund was decided and recorded against the payment.</summary>
    RefundRecorded = 1
}
