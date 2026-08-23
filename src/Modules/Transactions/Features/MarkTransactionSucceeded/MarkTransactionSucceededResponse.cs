using Transactions.Entities;
namespace Transactions.Features.MarkTransactionSucceeded;

public record MarkTransactionSucceededResponse
{
    public Guid TransactionId { get; init; }
    public Guid BookingId { get; init; }
    public TransactionStatus TransactionStatus { get; init; }
}
