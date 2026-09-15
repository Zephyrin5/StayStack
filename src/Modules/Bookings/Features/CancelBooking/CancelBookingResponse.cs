using Bookings.Entities;
using SeedWork.Enums;
using Transactions.Contracts;
namespace Bookings.Features.CancelBooking;

public record CancelBookingResponse
{
    public Guid BookingId { get; init; }
    public BookingStatus BookingStatus { get; init; }

    // Null when there is nothing to refund (no payment cleared, or no
    // transaction at all) - not the same as a real 0% tier, which is 0m. Once
    // a refund is recorded (RefundPending/Refunded/RefundFailed), the recorded
    // amount, read back rather than recomputed; while it is still owed, the
    // amount RefundDecision will record (docs/adr/0027).
    public decimal? RefundAmount { get; init; }

    // Null in lockstep with RefundAmount (docs/adr/0015).
    public Currency? Currency { get; init; }
    public decimal? RefundPercent { get; init; }

    // Where the refund stands: None (nothing to refund - RefundAmount,
    // Currency and RefundPercent are null alongside it), Pending (owed or
    // requested, not settled), Refunded, or Failed.
    //
    // A finished refund and a stuck one must be distinguishable - only one of
    // them means the guest's money is back.
    public RefundStatus RefundStatus { get; init; }

    // Kept for existing clients, derived so it cannot disagree with
    // RefundStatus. Prefer RefundStatus: false here still covers
    // None, Refunded and Failed alike.
    public bool RefundPending => RefundStatus == RefundStatus.Pending;
}
