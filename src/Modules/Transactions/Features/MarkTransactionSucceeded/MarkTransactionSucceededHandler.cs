using Bookings.Contracts;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Persistence;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Transactions.Contracts;
using Transactions.Entities;
using Transactions.Exceptions;
using System.Data;
namespace Transactions.Features.MarkTransactionSucceeded;

public class MarkTransactionSucceededHandler(
    TransactionsDb dbContext,
    ITransactionRunner transactionRunner,
    IBookingPaymentConfirmation bookingPaymentConfirmation,
    ITransactionReversal transactionReversal,
    TimeProvider timeProvider)
    : IRequestHandler<MarkTransactionSucceededRequest, MarkTransactionSucceededResponse>
{
    public async ValueTask<MarkTransactionSucceededResponse> Handle(
        MarkTransactionSucceededRequest request,
        CancellationToken cancellationToken)
    {
        int attempts = 0;

        // The payment, the booking's confirmation and its hold's sale - or, when
        // the payment bought nothing, its refund - commit together.
        return await transactionRunner.ExecuteAsync(
                IsolationLevel.ReadCommitted,
            async token =>
            {
                attempts++;

                Transaction transaction = await dbContext.Transactions
                                              .SingleOrDefaultAsync(t => t.Id == request.TransactionId, token)
                                          ?? throw new NotFoundException(nameof(Transaction), request.TransactionId);

                // Succeeded on a retry: an earlier attempt of this request committed
                // and lost its acknowledgement. MarkSucceeded would reject it as
                // already finalized and answer 409 for a success that happened. A
                // concurrent success committed in between reads the same, and is the
                // same fact. A first attempt never takes this branch, so a second
                // request still gets 409.
                if (attempts > 1 && transaction.SucceededAt is not null)
                {
                    return BuildResponse(transaction);
                }

                // In memory first: the guard rejects a transaction already out of
                // Pending before anything is locked.
                transaction.MarkSucceeded(timeProvider.GetUtcNow());

                // The booking before the transaction row (docs/adr/0028).
                // ConfirmPaymentAsync locks the booking and then its hold; the save
                // below writes the transaction row. CancelBookingHandler locks the
                // booking before resolving the refund that writes this row, so the
                // two paths cannot hold each other's locks.
                bool confirmed = await bookingPaymentConfirmation.ConfirmPaymentAsync(transaction.BookingId, token);

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
                    // visible at load time instead of at save time. The booking's
                    // confirmation rolls back with the scope.
                    throw new TransactionAlreadyFinalizedException(transaction.Id);
                }

                if (!confirmed)
                {
                    // The booking was cancelled, or its hold released, before this
                    // payment resolved: the payment bought nothing.
                    // RefundUnusablePayment rather than ResolveRefund: with no
                    // cancellation behind it, the whole amount is owed rather than
                    // nothing. By transaction id, so a booking with an earlier
                    // RefundPending attempt refunds this one.
                    //
                    // Records the refund decision locally. A provider call, when one
                    // exists, must not run inside this scope.
                    await transactionReversal.RefundUnusablePaymentByTransactionAsync(transaction.Id, token);
                }

                // The tracked instance, so a refund recorded just above is reflected.
                return BuildResponse(transaction);
            },
            cancellationToken);
    }

    private static MarkTransactionSucceededResponse BuildResponse(Transaction transaction) =>
        new MarkTransactionSucceededResponse
        {
            TransactionId = transaction.Id,
            BookingId = transaction.BookingId,
            TransactionStatus = transaction.TransactionStatus
        };
}
