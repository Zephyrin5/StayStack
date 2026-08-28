using Bookings.Contracts;
using BuildingBlocks.Exceptions;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Transactions.Entities;
using Transactions.Exceptions;
namespace Transactions.Features.InitiateTransaction;

public class InitiateTransactionHandler(
    AppTransactionsDbContext dbContext,
    IBookingLookup bookingLookup) : IRequestHandler<InitiateTransactionRequest, InitiateTransactionResponse>
{
    public async ValueTask<InitiateTransactionResponse> Handle(InitiateTransactionRequest request, CancellationToken cancellationToken)
    {
        BookingSummary booking = await bookingLookup.GetBookingAsync(request.BookingId, cancellationToken)
                                 ?? throw new NotFoundException("Booking", request.BookingId);

        if (!booking.IsPending)
        {
            throw new BookingNotPayableException(request.BookingId);
        }

        // A Pending or Succeeded transaction blocks a new one - Failed
        // leaves room for a retry, and Refunded/RefundPending/RefundFailed
        // are moot anyway since a booking only ever reaches those via
        // cancellation, which already fails the IsPending check above.
        // Spelled out as the exact active set, not "!= Failed" - the latter
        // would also match the refund states above by accident, relying on
        // the IsPending check above to make that harmless rather than
        // saying what's actually meant. This check is just a fast-path/
        // friendly-error optimization: it's not what actually prevents
        // double-charging under concurrent requests (two callers can both
        // pass it before either inserts) - the partial unique index in the
        // migration is the real authority, enforced below via the
        // DbUpdateException catch.
        bool hasTransactionInProgress = await dbContext.Transactions
            .AnyAsync(
                t => t.BookingId == request.BookingId
                     && (t.TransactionStatus == TransactionStatus.Pending || t.TransactionStatus == TransactionStatus.Succeeded),
                cancellationToken);

        if (hasTransactionInProgress)
        {
            throw new TransactionAlreadyInProgressException(request.BookingId);
        }

        Transaction transaction = Transaction.Create(request.BookingId, booking.TotalPrice);

        dbContext.Transactions.Add(transaction);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new TransactionAlreadyInProgressException(request.BookingId);
        }

        return new InitiateTransactionResponse
        {
            TransactionId = transaction.Id,
            BookingId = transaction.BookingId,
            Amount = transaction.Amount.Amount,
            Currency = transaction.Amount.Currency,
            TransactionStatus = transaction.TransactionStatus
        };
    }
}
