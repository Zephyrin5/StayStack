using BuildingBlocks.Exceptions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Transactions.Entities;
namespace Transactions.Features.MarkTransactionRefundFailed;

public class MarkTransactionRefundFailedHandler(AppTransactionsDbContext dbContext)
    : IRequestHandler<MarkTransactionRefundFailedRequest, MarkTransactionRefundFailedResponse>
{
    public async ValueTask<MarkTransactionRefundFailedResponse> Handle(
        MarkTransactionRefundFailedRequest request,
        CancellationToken cancellationToken)
    {
        Transaction transaction = await dbContext.Transactions
                                      .SingleOrDefaultAsync(t => t.Id == request.TransactionId, cancellationToken)
                                  ?? throw new NotFoundException(nameof(Transaction), request.TransactionId);

        transaction.MarkRefundFailed(request.Reason);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MarkTransactionRefundFailedResponse
        {
            TransactionId = transaction.Id,
            BookingId = transaction.BookingId,
            TransactionStatus = transaction.TransactionStatus
        };
    }
}
