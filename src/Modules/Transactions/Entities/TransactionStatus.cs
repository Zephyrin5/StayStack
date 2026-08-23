namespace Transactions.Entities;

// Lives here, not SeedWork - same reasoning as Bookings.Entities.BookingStatus:
// nothing outside this module needs it yet (Bookings.Contracts.BookingSummary
// deliberately exposes only IsPending, not a shared status enum).
public enum TransactionStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2
}
