using Mediator;
using Transactions.Entities;
namespace Transactions.Features.GetTransactions;

public record GetTransactionsRequest : IRequest<GetTransactionsResponse>
{
    public TransactionStatus? Status { get; init; }
}
