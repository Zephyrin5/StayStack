using BuildingBlocks.Pagination;
using Mediator;
using Transactions.Entities;
namespace Transactions.Features.GetTransactions;

public record GetTransactionsRequest : IRequest<PagedResponse<TransactionSummary>>
{
    public TransactionStatus? Status { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
