using BuildingBlocks.Exceptions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Transactions.Entities;
namespace Transactions.Features.MarkTransactionFailed;

public class MarkTransactionFailedHandler(AppTransactionsDbContext dbContext)
    : IRequestHandler<MarkTransactionFailedRequest, MarkTransactionFailedResponse>
{
    public async ValueTask<MarkTransactionFailedResponse> Handle(
        MarkTransactionFailedRequest request,
        CancellationToken cancellationToken)
    {
        Transaction transaction = await dbContext.Transactions
                                      .SingleOrDefaultAsync(t => t.Id == request.TransactionId, cancellationToken)
                                  ?? throw new NotFoundException(nameof(Transaction), request.TransactionId);

        // Booking is deliberately left untouched - it stays Pending, so a
        // customer can retry with a fresh InitiateTransaction call.
        transaction.MarkFailed(request.Reason);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new MarkTransactionFailedResponse
        {
            TransactionId = transaction.Id,
            BookingId = transaction.BookingId,
            TransactionStatus = transaction.TransactionStatus
        };
    }
}
