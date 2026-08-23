using Mediator;
namespace Transactions.Features.MarkTransactionFailed;

public record MarkTransactionFailedRequest : IRequest<MarkTransactionFailedResponse>
{
    public Guid TransactionId { get; init; }
    public string? Reason { get; init; }
}
