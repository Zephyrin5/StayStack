using Transactions.Entities;
namespace Transactions.Features.MarkTransactionRefundFailed;

public record MarkTransactionRefundFailedResponse
{
    public Guid TransactionId { get; init; }
    public Guid BookingId { get; init; }
    public TransactionStatus TransactionStatus { get; init; }
}
