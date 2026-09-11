using Outbox;
namespace Transactions.Outbox;

// The follow-up to MarkTransactionSucceededHandler's own authoritative write
// (transaction.MarkSucceeded()) - see docs/adr/0003.
public record ConfirmBookingPaymentOutboxMessage(Guid TransactionId, Guid BookingId) : IOutboxMessage
{
    // Persisted into rows that outlive the deployment that wrote them.
    // Renaming the record is free; changing this is not. See IOutboxMessage.
    public const string TypeName = "transactions.confirm-booking-payment.v1";
    static string IOutboxMessage.OutboxType => TypeName;
}
