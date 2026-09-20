using BuildingBlocks.Exceptions;
using BuildingBlocks.Persistence;
using Mediator;
using Microsoft.EntityFrameworkCore;
using System.Data;
using Transactions.Contracts;
using Transactions.Entities;
using Transactions.Exceptions;
namespace Transactions.Features.MarkTransactionFailed;

public class MarkTransactionFailedHandler(
    TransactionsDb dbContext,
    ITransactionRunner transactionRunner,
    TransactionReversal transactionReversal)
    : IRequestHandler<MarkTransactionFailedRequest, MarkTransactionFailedResponse>
{
    public async ValueTask<MarkTransactionFailedResponse> Handle(
        MarkTransactionFailedRequest request,
        CancellationToken cancellationToken)
    {
        int attempts = 0;

        // The failure and the obligation it settles commit together: a failed payment on a cancelled
        // booking is the last thing that could have paid it, and saying so here is what keeps the row
        // out of the sweep (docs/adr/0027).
        return await transactionRunner.ExecuteAsync(
            IsolationLevel.ReadCommitted,
            async token =>
            {
                attempts++;

                Transaction transaction = await dbContext.Transactions
                                              .SingleOrDefaultAsync(t => t.Id == request.TransactionId, token)
                                          ?? throw new NotFoundException(nameof(Transaction), request.TransactionId);

                // Already Failed on a retry: an earlier attempt of this request committed and lost its
                // acknowledgement. MarkFailed would answer 409 for a failure that happened. A first
                // attempt never takes this branch, so a second request still gets 409.
                if (attempts > 1 && transaction.TransactionStatus == TransactionStatus.Failed)
                {
                    return BuildResponse(transaction);
                }

                // Booking is deliberately left untouched - it stays Pending, so a
                // customer can retry with a fresh InitiateTransaction call.
                transaction.MarkFailed(request.Reason);

                try
                {
                    await dbContext.SaveChangesAsync(token);
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

                // A no-op unless the booking was cancelled and its obligation is still open. With this
                // payment failed, whether anything is owed depends on what other payments remain, which
                // the resolver is the one place that decides.
                await transactionReversal.ResolveRefundAsync(transaction.BookingId, token);

                return BuildResponse(transaction);
            },
            cancellationToken);
    }

    private static MarkTransactionFailedResponse BuildResponse(Transaction transaction) =>
        new MarkTransactionFailedResponse
        {
            TransactionId = transaction.Id,
            BookingId = transaction.BookingId,
            TransactionStatus = transaction.TransactionStatus
        };
}
