using Bookings.Entities;
using SeedWork.Enums;
using Transactions.Contracts;
namespace Bookings.Features.CancelBooking;

public record CancelBookingResponse
{
    public Guid BookingId { get; init; }
    public BookingStatus BookingStatus { get; init; }

    // Null when there is nothing to refund, which a real 0% tier (0m) is not. Once recorded, the
    // recorded amount; while owed, the amount RefundDecision will record (docs/adr/0027).
    public decimal? RefundAmount { get; init; }

    // Null in lockstep with RefundAmount (docs/adr/0015).
    public Currency? Currency { get; init; }
    public decimal? RefundPercent { get; init; }

    // None leaves RefundAmount, Currency and RefundPercent null.
    public RefundStatus RefundStatus { get; init; }
}
