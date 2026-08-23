using Mediator;
using Microsoft.EntityFrameworkCore;
using Transactions.Entities;
namespace Transactions.Features.GetTransactions;

public class GetTransactionsHandler(AppTransactionsDbContext dbContext) : IRequestHandler<GetTransactionsRequest, GetTransactionsResponse>
{
    public async ValueTask<GetTransactionsResponse> Handle(GetTransactionsRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.Transactions.AsNoTracking();

        if (request.Status is not null)
        {
            query = query.Where(t => t.TransactionStatus == request.Status);
        }

        List<Transaction> transactions = await query
            .OrderByDescending(t => t.CreatedAt)
            .ToListAsync(cancellationToken);

        return new GetTransactionsResponse
        {
            Transactions =
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
            ]
        };
    }
}
