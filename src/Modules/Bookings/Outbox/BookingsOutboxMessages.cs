using Outbox;
using SeedWork.Enums;
namespace Bookings.Outbox;

// CancelBookingHandler enqueues ReverseTransaction and ReverseRedemption after
// a durable cancel; ExpireUnpaidBookingsJob enqueues ReverseRedemption. Nothing
// enqueues ReleaseHold; the dispatcher still delivers it, and it goes with the
// outbox - see docs/adr/0003.
//
// Each TypeName below is persisted into rows that outlive the deployment that
// wrote them. Renaming one of these records is free; changing its TypeName
// strands every undelivered row of that type. See IOutboxMessage.

public record ReleaseHoldOutboxMessage(Guid HoldId) : IOutboxMessage
{
    public const string TypeName = "bookings.release-hold.v1";
    static string IOutboxMessage.OutboxType => TypeName;
}

// Carries only the booking id now. The amount, the currency and the
// cancellation moment all live on the RefundObligation this message asks
// someone to resolve, so repeating them here was three chances to disagree
// with the row that decides.
//
// Removing fields keeps the .v1 identifier. System.Text.Json ignores
// properties it does not recognise, so a queued row written with the wider
// payload still deserializes - and there is no deployed database carrying any.
public record ReverseTransactionOutboxMessage(Guid BookingId) : IOutboxMessage
{
    public const string TypeName = "bookings.reverse-transaction.v1";
    static string IOutboxMessage.OutboxType => TypeName;
}

public record ReverseRedemptionOutboxMessage(Guid BookingId) : IOutboxMessage
{
    public const string TypeName = "bookings.reverse-promotion-redemption.v1";
    static string IOutboxMessage.OutboxType => TypeName;
}
