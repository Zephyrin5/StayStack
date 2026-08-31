using Bookings.Entities;
using SeedWork.Enums;
namespace Bookings.Features.CancelBooking;

public record CancelBookingResponse
{
    public Guid BookingId { get; init; }
    public BookingStatus BookingStatus { get; init; }

    // Null when there was nothing Succeeded to refund (no payment had
    // cleared yet, or there was never a transaction at all) - not the same
    // as a real 0% tier, which still returns 0m here, not null. The
    // requested/computed figure from the cancellation policy, not a
    // read-back of what ReverseTransactionAsync's own outbox dispatch
    // actually did - see RefundPending below for why, and
    // ITransactionReversal.GetSucceededTransactionAmountAsync's own doc
    // comment for the gap this closes.
    public decimal? RefundAmount { get; init; }

    // Added alongside RefundAmount - previously this response reported a
    // money amount with no currency at all, an outright gap rather than a
    // deliberate omission (see docs/adr/0015). Same null-in-lockstep
    // reasoning as RefundAmount.
    public Currency? Currency { get; init; }
    public decimal? RefundPercent { get; init; }

    // True until the outbox-dispatched reversal is confirmed to have
    // landed (checked via ITransactionReversal.GetRefundSnapshotAsync right
    // before this response is built) - on the overwhelmingly common path
    // where the inline dispatch attempt succeeds within this same request,
    // this is already false by the time the caller sees it. False (with
    // RefundAmount/Currency/RefundPercent all null) when there was nothing
    // to refund in the first place - not to be confused with "still
    // pending".
    public bool RefundPending { get; init; }
}
