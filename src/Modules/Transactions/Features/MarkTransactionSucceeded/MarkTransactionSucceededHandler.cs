using Bookings.Contracts;
using BuildingBlocks.Exceptions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Transactions.Entities;
namespace Transactions.Features.MarkTransactionSucceeded;

public class MarkTransactionSucceededHandler(
    AppTransactionsDbContext dbContext,
    IBookingPaymentConfirmation bookingPaymentConfirmation)
    : IRequestHandler<MarkTransactionSucceededRequest, MarkTransactionSucceededResponse>
{
    public async ValueTask<MarkTransactionSucceededResponse> Handle(
        MarkTransactionSucceededRequest request,
        CancellationToken cancellationToken)
    {
        Transaction transaction = await dbContext.Transactions
                                      .SingleOrDefaultAsync(t => t.Id == request.TransactionId, cancellationToken)
                                  ?? throw new NotFoundException(nameof(Transaction), request.TransactionId);

        // Marks the transaction itself first - this is the source of
        // truth for "payment actually succeeded". If the booking-side
        // write below fails, nothing is lost: the transaction stays
        // Succeeded and BookingPaymentConfirmation can be retried, unlike
        // the reverse order which could leave a Confirmed booking behind
        // a transaction that was never actually recorded as paid. See
        // docs/adr/0003 for the authoritative-write-first ordering this
        // follows.
        transaction.MarkSucceeded();
        await dbContext.SaveChangesAsync(cancellationToken);

        bool confirmed = await bookingPaymentConfirmation.ConfirmPaymentAsync(transaction.BookingId, cancellationToken);

        if (!confirmed)
        {
            // The booking was already cancelled by the time this payment
            // resolved - money came in for something nobody wants anymore.
            // The payment succeeding is still a fact (TransactionStatus
            // stays Succeeded above, it isn't undone), it just immediately
            // needs reversing rather than being left to look like ordinary
            // completed revenue.
            transaction.MarkRefundPending();
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new MarkTransactionSucceededResponse
        {
            TransactionId = transaction.Id,
            BookingId = transaction.BookingId,
            TransactionStatus = transaction.TransactionStatus
        };
    }
}
