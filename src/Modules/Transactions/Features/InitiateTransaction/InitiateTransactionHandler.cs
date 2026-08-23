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

        // A Pending or already-Succeeded transaction blocks a new one -
        // only a Failed transaction leaves room for a retry. This check is
        // just a fast-path/friendly-error optimization: it's not what
        // actually prevents double-charging under concurrent requests (two
        // callers can both pass it before either inserts) - the partial
        // unique index in the migration is the real authority, enforced
        // below via the DbUpdateException catch.
        bool hasTransactionInProgress = await dbContext.Transactions
            .AnyAsync(t => t.BookingId == request.BookingId && t.TransactionStatus != TransactionStatus.Failed, cancellationToken);

        if (hasTransactionInProgress)
        {
            throw new TransactionAlreadyInProgressException(request.BookingId);
        }

        Transaction transaction = Transaction.Create(request.BookingId, booking.TotalPrice, booking.Currency);

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
            Amount = transaction.Amount,
            Currency = transaction.Currency,
            TransactionStatus = transaction.TransactionStatus
        };
    }
}
