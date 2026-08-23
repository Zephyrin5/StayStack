using Mediator;
namespace Transactions.Features.MarkTransactionSucceeded;

public record MarkTransactionSucceededRequest : IRequest<MarkTransactionSucceededResponse>
{
    public Guid TransactionId { get; init; }
}
