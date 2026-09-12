using Outbox;
using SeedWork.Enums;
namespace Bookings.Outbox;

// Shared by CancelBookingHandler (all three, as the follow-up to a durable
// cancel) and ConfirmBookingHandler (ReleaseHold/ReverseRedemption only, as
// the compensation when its own booking-save or promo-redemption fails) -
// see docs/adr/0003.
//
// Each TypeName below is persisted into rows that outlive the deployment that
// wrote them. Renaming one of these records is free; changing its TypeName
// strands every undelivered row of that type. See IOutboxMessage.

public record ReleaseHoldOutboxMessage(Guid HoldId) : IOutboxMessage
{
    public const string TypeName = "bookings.release-hold.v1";
    static string IOutboxMessage.OutboxType => TypeName;
}

// CancelledAt is nullable and added without a version bump, which the
// IOutboxMessage contract allows: an added optional field deserializes as null
// on rows written by an older deployment, and the refund rule treats a null as
// "the payment came first" - the behaviour those rows already had.
public record ReverseTransactionOutboxMessage(
    Guid BookingId, decimal RefundAmount, Currency Currency, DateTimeOffset? CancelledAt = null) : IOutboxMessage
{
    public const string TypeName = "bookings.reverse-transaction.v1";
    static string IOutboxMessage.OutboxType => TypeName;
}

public record ReverseRedemptionOutboxMessage(Guid BookingId) : IOutboxMessage
{
    public const string TypeName = "bookings.reverse-promotion-redemption.v1";
    static string IOutboxMessage.OutboxType => TypeName;
}
