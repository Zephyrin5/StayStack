using Mediator;
namespace Transactions.Features.MarkTransactionRefunded;

public record MarkTransactionRefundedRequest : IRequest<MarkTransactionRefundedResponse>
{
    public Guid TransactionId { get; init; }
}
