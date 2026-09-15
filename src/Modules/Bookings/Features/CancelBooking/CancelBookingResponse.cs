using Bookings.Entities;
using SeedWork.Enums;
using Transactions.Contracts;
namespace Bookings.Features.CancelBooking;

public record CancelBookingResponse
{
    public Guid BookingId { get; init; }
    public BookingStatus BookingStatus { get; init; }

    // Null when there was nothing Succeeded to refund (no payment had
    // cleared yet, or there was never a transaction at all) - not the same
    // as a real 0% tier, which still returns 0m here, not null. When a
    // refund has already reached the refund sub-lifecycle (RefundPending/
    // Refunded/RefundFailed), this is a read-back of the real recorded
    // amount (ITransactionReversal.GetRefundSnapshotAsync), not a fresh
    // recomputation - that snapshot is authoritative over anything derived
    // from today's date. Only while still waiting for the reversal to even
    // start is this the requested/computed figure instead. See
    // RefundPending below.
    public decimal? RefundAmount { get; init; }

    // Null in lockstep with RefundAmount (docs/adr/0015).
    public Currency? Currency { get; init; }
    public decimal? RefundPercent { get; init; }

    // Where the refund stands: None (nothing to refund - RefundAmount,
    // Currency and RefundPercent are null alongside it), Pending (owed or
    // requested, not settled), Refunded, or Failed.
    //
    // Failed is the reason this exists. The boolean below was false for a
    // refund that had gone back to the card and for one the provider had
    // refused, so a guest reading it could not tell a finished refund from a
    // stuck one - and only one of those means their money is back.
    public RefundStatus RefundStatus { get; init; }

    // Kept for existing clients, and now derived rather than set, so it cannot
    // disagree with RefundStatus. Prefer RefundStatus: false here still covers
    // None, Refunded and Failed alike.
    public bool RefundPending => RefundStatus == RefundStatus.Pending;
}
