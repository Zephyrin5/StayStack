using BuildingBlocks.Exceptions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Transactions.Entities;
using Transactions.Exceptions;
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

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
        // A concurrent finalization, not an infrastructure fault. The row's
        // xmin moved between this load and this save (see
        // TransactionConfiguration), so the UPDATE matched nothing - which
        // means another caller took this transaction out of Pending while
        // this one was deciding. That is precisely what
        // TransactionAlreadyFinalizedException already describes, and it is
        // the same 409 the in-memory guard raises when the conflict is
        // visible at load time instead of at save time.
            throw new TransactionAlreadyFinalizedException(transaction.Id);
        }

        return new MarkTransactionFailedResponse
        {
            TransactionId = transaction.Id,
            BookingId = transaction.BookingId,
            TransactionStatus = transaction.TransactionStatus
        };
    }
}
