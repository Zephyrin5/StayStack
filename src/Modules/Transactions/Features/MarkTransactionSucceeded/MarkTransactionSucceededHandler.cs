using BuildingBlocks.Exceptions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Outbox;
using Transactions.Entities;
using Transactions.Exceptions;
using Transactions.Outbox;
using Transactions.Serialization;
namespace Transactions.Features.MarkTransactionSucceeded;

public class MarkTransactionSucceededHandler(
    AppTransactionsDbContext dbContext,
    TransactionsOutboxDispatcher dispatcher,
    TimeProvider timeProvider)
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
        transaction.MarkSucceeded(timeProvider.GetUtcNow());

        OutboxMessage confirmPaymentRow = dispatcher.Enqueue(
            new ConfirmBookingPaymentOutboxMessage(transaction.Id, transaction.BookingId),
            TransactionsJsonSerializerContext.Default.ConfirmBookingPaymentOutboxMessage);

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
            //
            // The enqueued outbox row rolls back with it, which is correct:
            // the confirmation it authorises never happened.
            throw new TransactionAlreadyFinalizedException(transaction.Id);
        }

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
