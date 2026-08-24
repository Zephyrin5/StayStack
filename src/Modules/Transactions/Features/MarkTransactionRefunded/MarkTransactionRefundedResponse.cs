using Transactions.Entities;
namespace Transactions.Features.MarkTransactionRefunded;

public record MarkTransactionRefundedResponse
{
    public Guid TransactionId { get; init; }
    public Guid BookingId { get; init; }
    public TransactionStatus TransactionStatus { get; init; }
}
