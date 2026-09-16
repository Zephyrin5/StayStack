using Bookings.Contracts;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Persistence;
using Dapper;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Persistence;
using Transactions.Entities;
using Transactions.Entities.Configurations;
using Transactions.Exceptions;
using System.Data;
namespace Transactions.Features.InitiateTransaction;

public class InitiateTransactionHandler(
    TransactionsDb dbContext,
    ITransactionRunner transactionRunner,
    IBookingLookup bookingLookup,
    ICurrentUserProvider currentUserProvider) : IRequestHandler<InitiateTransactionRequest, InitiateTransactionResponse>
{
    public async ValueTask<InitiateTransactionResponse> Handle(InitiateTransactionRequest request, CancellationToken cancellationToken)
    {
        // Ownership first: without it anyone with a booking id could block the guest's payment.
        BookingAccessResult booking = await bookingLookup.VerifyBookingAccessAsync(
                                          request.BookingId,
                                          currentUserProvider.UserId,
                                          cancellationToken)
                                      ?? throw new NotFoundException("Booking", request.BookingId);

        if (!booking.IsPending)
        {
            throw new BookingNotPayableException(request.BookingId);
        }

        // Minted before the retry so a lost acknowledgement finds its own row (docs/adr/0025).
        Guid transactionId = Guid.CreateVersion7();

        return await transactionRunner.ExecuteAsync(
                IsolationLevel.ReadCommitted,
            async token =>
            {
                // Excludes a cancellation committing between the payability check and the insert (docs/adr/0028).
                await dbContext.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
                    AdvisoryLock.AcquireExclusiveSql,
                    new { LockKey = BookingPaymentLock.KeyFor(request.BookingId) },
                    dbContext.Database.CurrentTransaction!.GetDbTransaction(),
                    cancellationToken: token));

                // Before the checks below, which would otherwise judge the request against its own committed row.
                Transaction? committed = await dbContext.Transactions.AsNoTracking()
                    .SingleOrDefaultAsync(t => t.Id == transactionId, token);

                if (committed is not null)
                {
                    return BuildResponse(committed);
                }

                BookingAccessResult? current = await bookingLookup.GetBookingDetailsAsync(request.BookingId, token);

                if (current is null || !current.IsPending)
                {
                    throw new BookingNotPayableException(request.BookingId);
                }

                return await InsertAsync(transactionId, request, current, token);
            },
            cancellationToken);
    }

    private async ValueTask<InitiateTransactionResponse> InsertAsync(
        Guid transactionId, InitiateTransactionRequest request, BookingAccessResult booking,
        CancellationToken cancellationToken)
    {
        // A friendly early answer; the active-transaction index is the authority.
        bool hasTransactionInProgress = await dbContext.Transactions
            .AnyAsync(
                t => t.BookingId == request.BookingId
                     && (t.TransactionStatus == TransactionStatus.Pending || t.TransactionStatus == TransactionStatus.Succeeded),
                cancellationToken);

        if (hasTransactionInProgress)
        {
            throw new TransactionAlreadyInProgressException(request.BookingId);
        }

        Transaction transaction = Transaction.Create(transactionId, request.BookingId, booking.TotalPrice);
        dbContext.Transactions.Add(transaction);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.IsViolationOf(TransactionConfiguration.ActiveTransactionIndex))
        {
            throw new TransactionAlreadyInProgressException(request.BookingId);
        }

        return BuildResponse(transaction);
    }

    private static InitiateTransactionResponse BuildResponse(Transaction transaction) =>
        new InitiateTransactionResponse
        {
            TransactionId = transaction.Id,
            BookingId = transaction.BookingId,
            Amount = transaction.Amount.Amount,
            Currency = transaction.Amount.Currency,
            TransactionStatus = transaction.TransactionStatus
        };
}
