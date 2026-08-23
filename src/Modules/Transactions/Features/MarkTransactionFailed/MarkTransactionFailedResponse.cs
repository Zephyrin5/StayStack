using Transactions.Entities;
namespace Transactions.Features.MarkTransactionFailed;

public record MarkTransactionFailedResponse
{
    public Guid TransactionId { get; init; }
    public Guid BookingId { get; init; }
    public TransactionStatus TransactionStatus { get; init; }
}
