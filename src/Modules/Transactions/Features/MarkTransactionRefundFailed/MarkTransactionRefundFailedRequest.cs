using Mediator;
namespace Transactions.Features.MarkTransactionRefundFailed;

public record MarkTransactionRefundFailedRequest : IRequest<MarkTransactionRefundFailedResponse>
{
    public Guid TransactionId { get; init; }
    public string? Reason { get; init; }
}
