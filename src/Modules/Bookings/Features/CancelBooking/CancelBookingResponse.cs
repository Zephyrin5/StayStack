using Bookings.Entities;
using SeedWork.Enums;
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

    // Added alongside RefundAmount - previously this response reported a
    // money amount with no currency at all, an outright gap rather than a
    // deliberate omission (see docs/adr/0015). Same null-in-lockstep
    // reasoning as RefundAmount.
    public Currency? Currency { get; init; }
    public decimal? RefundPercent { get; init; }

    // True only in the narrow window where a Succeeded transaction exists
    // but ITransactionReversal.GetRefundSnapshotAsync can't yet confirm the
    // reversal landed - on the overwhelmingly common path, where the
    // inline dispatch attempt succeeds within this same request, this is
    // already false by the time the caller sees it. Also false (with
    // RefundAmount/Currency/RefundPercent all null) when there was nothing
    // to refund in the first place - not to be confused with "still
    // pending".
    public bool RefundPending { get; init; }
}
