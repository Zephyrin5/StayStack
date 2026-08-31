namespace Transactions.Entities;

// Lives here, not SeedWork - same reasoning as Bookings.Entities.BookingStatus:
// nothing outside this module needs it yet (Bookings.Contracts.BookingSummary
// deliberately exposes only IsPending, not a shared status enum).
public enum TransactionStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2,

    // Mirrors the Pending -> {Succeeded, Failed} shape for the refund
    // sub-lifecycle a cancelled booking's Succeeded transaction enters -
    // see Transaction.MarkRefundPending()/MarkRefunded()/MarkRefundFailed(),
    // and the admin stand-in endpoints that resolve RefundPending, same as
    // MarkTransactionSucceeded/MarkTransactionFailed standing in for a
    // real gateway webhook.
    RefundPending = 3,
    Refunded = 4,
    RefundFailed = 5
}
