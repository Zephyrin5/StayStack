using BuildingBlocks.Exceptions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Outbox;
using Transactions.Entities;
using Transactions.Outbox;
using Transactions.Serialization;
namespace Transactions.Features.MarkTransactionSucceeded;

public class MarkTransactionSucceededHandler(
    AppTransactionsDbContext dbContext,
    TransactionsOutboxDispatcher dispatcher)
    : IRequestHandler<MarkTransactionSucceededRequest, MarkTransactionSucceededResponse>
{
    public async ValueTask<MarkTransactionSucceededResponse> Handle(
        MarkTransactionSucceededRequest request,
        CancellationToken cancellationToken)
    {
        Transaction transaction = await dbContext.Transactions
                                      .SingleOrDefaultAsync(t => t.Id == request.TransactionId, cancellationToken)
                                  ?? throw new NotFoundException(nameof(Transaction), request.TransactionId);

        // Marks the transaction itself first - the source of truth for
        // "payment actually succeeded". The booking-side confirmation
        // below is an outbox message enqueued in this same SaveChangesAsync:
        // a crash after this commits leaves both the Succeeded status and
        // the durable intent to confirm the booking together, closing a
        // real gap - a crash between this and a direct ConfirmPaymentAsync
        // call used to leave Succeeded + booking Pending forever, with no
        // retry path (MarkSucceeded's own guard rejects a retry with 409
        // once Succeeded is set).
        transaction.MarkSucceeded();

        OutboxMessage confirmPaymentRow = dispatcher.Enqueue(
            new ConfirmBookingPaymentOutboxMessage(transaction.Id, transaction.BookingId),
            TransactionsJsonSerializerContext.Default.ConfirmBookingPaymentOutboxMessage);

        await dbContext.SaveChangesAsync(cancellationToken);

        // The confirmed-vs-refund-pending branch that used to live here now
        // lives in TransactionsOutboxDispatcher.TryHandleAsync, since it
        // depends on ConfirmPaymentAsync's result - see its own comment.
        await dispatcher.TryDispatchAsync(confirmPaymentRow, cancellationToken);

        // transaction.TransactionStatus below reflects whatever the dispatch
        // above just did (still Succeeded, or moved to RefundPending) - not
        // a stale read, because the dispatcher loads the same tracked
        // Transaction instance through this same DbContext (EF's identity
        // resolution returns the already-tracked object rather than a new
        // one), so this local reference was mutated in place.
        return new MarkTransactionSucceededResponse
        {
            TransactionId = transaction.Id,
            BookingId = transaction.BookingId,
            TransactionStatus = transaction.TransactionStatus
        };
    }
}
