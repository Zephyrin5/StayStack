using BuildingBlocks.Exceptions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Transactions.Entities;
namespace Transactions.Features.MarkTransactionRefunded;

public class MarkTransactionRefundedHandler(AppTransactionsDbContext dbContext)
    : IRequestHandler<MarkTransactionRefundedRequest, MarkTransactionRefundedResponse>
{
    public async ValueTask<MarkTransactionRefundedResponse> Handle(
        MarkTransactionRefundedRequest request,
        CancellationToken cancellationToken)
    {
        Transaction transaction = await dbContext.Transactions
                                      .SingleOrDefaultAsync(t => t.Id == request.TransactionId, cancellationToken)
                                  ?? throw new NotFoundException(nameof(Transaction), request.TransactionId);

        transaction.MarkRefunded();
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MarkTransactionRefundedResponse
        {
            TransactionId = transaction.Id,
            BookingId = transaction.BookingId,
            TransactionStatus = transaction.TransactionStatus
        };
    }
}
