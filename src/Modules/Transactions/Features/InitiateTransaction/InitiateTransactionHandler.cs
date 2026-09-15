using Bookings.Contracts;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Mediator;
using BuildingBlocks.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using Transactions.Entities;
using Transactions.Entities.Configurations;
using Persistence;
using Transactions.Exceptions;
namespace Transactions.Features.InitiateTransaction;

public class InitiateTransactionHandler(
    AppTransactionsDbContext dbContext,
    IBookingLookup bookingLookup,
    ICurrentUserProvider currentUserProvider) : IRequestHandler<InitiateTransactionRequest, InitiateTransactionResponse>
{
    public async ValueTask<InitiateTransactionResponse> Handle(InitiateTransactionRequest request, CancellationToken cancellationToken)
    {
        // Ownership proof first, via the same two-path check
        // CancelBookingHandler and GetBookingForManagementHandler use: a
        // matching CustomerId (authenticated) or a valid management token
        // (guest checkout). Without it, anyone holding a booking id could open a
        // Pending transaction, and the partial unique index would reject the
        // real guest's payment with 409.
        //
        // The 404-vs-409 split below is kept: it would be a status oracle only
        // if anyone could ask, and an owner is entitled to know why their own
        // booking cannot be paid for.
        BookingAccessResult booking = await bookingLookup.VerifyBookingAccessAsync(
                                          request.BookingId,
                                          currentUserProvider.UserId,
                                          cancellationToken)
                                      ?? throw new NotFoundException("Booking", request.BookingId);

        if (!booking.IsPending)
        {
            throw new BookingNotPayableException(request.BookingId);
        }

        // Everything from here runs under the booking's payment lock. The check
        // above and the insert below are otherwise independent, so a
        // cancellation committing between them leaves a pending payment against
        // a cancelled booking - and if that cancellation moved an earlier payment
        // to RefundPending, the booking ends up with a RefundPending and a
        // Succeeded transaction at once.
        //
        // This path takes no other lock. Paths that take it with the booking row
        // lock take it first (docs/adr/0028).
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        // Once, outside the retry (docs/adr/0025). A fresh id per attempt would
        // make a retry after a lost acknowledgement find its own row as "a
        // transaction already in progress" and answer 409 to the guest who just
        // created it.
        Guid transactionId = Guid.CreateVersion7();

        return await strategy.ExecuteAsync(async () =>
        {
            dbContext.ChangeTracker.Clear();

            await using IDbContextTransaction scope =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
            {
                await dbContext.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
                    AdvisoryLock.AcquireExclusiveSql,
                    new { LockKey = BookingPaymentLock.KeyFor(request.BookingId) },
                    scope.GetDbTransaction(),
                    cancellationToken: cancellationToken));
            }

            // An earlier attempt of this request may already have committed.
            //
            // Asked under the lock, and that is what makes one read enough. An
            // earlier attempt held this same lock until its transaction ended,
            // and Postgres makes a commit visible before it releases that
            // transaction's locks - so by the time this attempt holds the lock,
            // the earlier one has either committed visibly or rolled back. There
            // is no in-flight commit left to race.
            //
            // Asked first - before the payability re-read and before the
            // active-transaction check - because both would otherwise judge the
            // request against a world containing its own row: the active check
            // refuses it as "already in progress", and a cancellation that
            // landed since would refuse it as not payable. Neither is true of
            // an operation that has already happened. The commit is the
            // outcome; what follows it is somebody else's concern, and a payment
            // against a booking cancelled afterwards is exactly what the refund
            // obligation exists to settle.
            Transaction? committed = await dbContext.Transactions.AsNoTracking()
                .SingleOrDefaultAsync(t => t.Id == transactionId, cancellationToken);

            if (committed is not null)
            {
                await scope.RollbackAsync(cancellationToken);
                return BuildResponse(committed);
            }

            // Re-read under the lock: the lock orders this against a
            // cancellation, and only a read says what that cancellation did.
            BookingAccessResult? current = await bookingLookup.GetBookingDetailsAsync(
                request.BookingId, cancellationToken);

            if (current is null || !current.IsPending)
            {
                throw new BookingNotPayableException(request.BookingId);
            }

            InitiateTransactionResponse created =
                await InsertAsync(transactionId, request, current, cancellationToken);

            await scope.CommitAsync(cancellationToken);
            return created;
        });
    }

    private async ValueTask<InitiateTransactionResponse> InsertAsync(
        Guid transactionId, InitiateTransactionRequest request, BookingAccessResult booking,
        CancellationToken cancellationToken)
    {
        // A Pending or Succeeded transaction blocks a new one - Failed
        // leaves room for a retry, and the refund states are moot since a
        // booking only reaches those via cancellation, which already fails
        // IsPending above. Spelled out as the exact active set, not
        // "!= Failed" - that would also match the refund states by
        // accident. This check is just a fast-path/friendly-error
        // optimization: it doesn't prevent double-charging under
        // concurrent requests (two callers can both pass it before either
        // inserts) - the partial unique index below is the real
        // authority, enforced via the DbUpdateException catch.
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
            // Another transaction for this booking is already Pending or
            // Succeeded. Any other violation propagates: this request's own
            // committed row is found by the lookup at the top of the delegate,
            // under the payment lock, before an insert is attempted.
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
