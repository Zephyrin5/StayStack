using SeedWork.Enums;
namespace Bookings.Outbox;

// Shared by CancelBookingHandler (all three, as the follow-up to a durable
// cancel) and ConfirmBookingHandler (ReleaseHold/ReverseRedemption only, as
// the compensation when its own booking-save or promo-redemption fails) -
// see docs/adr/0003.

public record ReleaseHoldOutboxMessage(Guid HoldId);

public record ReverseTransactionOutboxMessage(Guid BookingId, decimal RefundAmount, Currency Currency);

public record ReverseRedemptionOutboxMessage(Guid BookingId);
