using BuildingBlocks.Pagination;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Transactions.Entities;
namespace Transactions.Features.GetTransactions;

public class GetTransactionsHandler(AppTransactionsDbContext dbContext) : IRequestHandler<GetTransactionsRequest, PagedResponse<TransactionSummary>>
{
    public async ValueTask<PagedResponse<TransactionSummary>> Handle(GetTransactionsRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.Transactions.AsNoTracking();

        if (request.Status is not null)
        {
            query = query.Where(t => t.TransactionStatus == request.Status);
        }

        // Id as a tiebreaker, not a sort criterion - see docs/adr/0008.
        (List<Transaction> transactions, int totalCount) = await query
            .OrderByDescending(t => t.CreatedAt).ThenBy(t => t.Id)
            .ToPagedListAsync(request.Page, request.PageSize, cancellationToken);

        return new PagedResponse<TransactionSummary>
        {
            Items =
            [
                .. transactions.Select(t => new TransactionSummary
                {
                    TransactionId = t.Id,
                    BookingId = t.BookingId,
                    Amount = t.Amount,
                    Currency = t.Currency,
                    TransactionStatus = t.TransactionStatus,
                    FailureReason = t.FailureReason,
                    CreatedAt = t.CreatedAt
                })
            ],
            Page = request.Page,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }
}
