namespace Transactions.Outbox;

// The follow-up to MarkTransactionSucceededHandler's own authoritative write
// (transaction.MarkSucceeded()) - see docs/adr/0003.
public record ConfirmBookingPaymentOutboxMessage(Guid TransactionId, Guid BookingId);
