using Bookings.Contracts;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Mediator;
using BuildingBlocks.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Transactions.Entities;
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
        // (guest checkout). Not distinguishing "doesn't exist" from "isn't
        // yours" is BookingAccessChecker's own contract.
        //
        // This endpoint used to call the unauthenticated GetBookingAsync with
        // nothing but the id, alone among the anonymous booking-scoped
        // endpoints. Two consequences, both closed by this:
        //
        // - The 404-vs-409 split below was a status oracle for a booking id.
        //   Impractical to enumerate (Guid v7 carries 74 random bits), but the
        //   codebase is careful about exactly this elsewhere -
        //   HostAuthorization.RequireOwnership returns 404 rather than 403 for
        //   the same reason.
        // - Worse than the oracle: anyone holding an id could open a Pending
        //   transaction on it, and the partial unique index would then reject
        //   the real guest's payment with 409. A payment-denial vector.
        //
        // The split itself is KEPT, deliberately. It is only an oracle when
        // anyone can ask; a caller who has proven ownership is entitled to
        // know why their own booking cannot be paid for, and "not found" for
        // a booking they are looking at would be actively misleading.
        BookingAccessResult booking = await bookingLookup.VerifyBookingAccessAsync(
                                          request.BookingId,
                                          currentUserProvider.UserId,
                                          cancellationToken)
                                      ?? throw new NotFoundException("Booking", request.BookingId);

        if (!booking.IsPending)
        {
            throw new BookingNotPayableException(request.BookingId);
        }

        // Everything from here runs under the booking's payment lock, and that
        // is the whole of this change.
        //
        // The check above and the insert below used to be independent
        // operations, so a cancellation committing between them produced a
        // pending payment against a cancelled booking. Untidy on its own - no
        // money moves - but it also manufactures a state the refund resolver
        // cannot read: if that cancellation moved an earlier payment to
        // RefundPending, the active-transaction check below matches nothing,
        // this insert succeeds, and the booking ends up with a RefundPending
        // and a Succeeded transaction at once. The active index permits that
        // pair, and any booking-wide query that assumes one row then throws on
        // every retry and every sweep pass, forever.
        //
        // The payment lock is the only lock this path takes, so it has no
        // ordering of its own to get wrong. The paths that take it alongside the
        // booking row lock take it first - see BookingPaymentLock.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        // Once, outside the retry. Generated inside, as it used to be, every
        // attempt minted a new id - so an attempt whose commit lost its
        // acknowledgement was followed by one that could not recognise the row
        // it had written, found it as "a transaction already in progress", and
        // answered 409 to the guest who had just created it. A primary-key
        // recovery existed for exactly this case and could never fire, which
        // was worse than its absence: it read as handled.
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

            // Re-read under the lock. Taking the lock only orders this against
            // a cancellation - it says nothing about what that cancellation did
            // before this got here, which is the same lesson the archival path
            // learned.
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
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation)
        {
            // By constraint name, never by SqlState alone: a unique violation
            // is only a conflict when it is the index that means one.
            //
            // ix_transactions_booking_id_active is the real conflict: another
            // transaction for this booking is already Pending or Succeeded.
            // Anything else is a violation nobody anticipated, and guessing what
            // it means would report an outcome for a write that never happened.
            // Let it surface.
            //
            // That includes the primary key. There used to be a recovery branch
            // for it - "the retry re-inserted our own committed row, read it
            // back" - and it was unreachable twice over: first because the id
            // was minted per attempt, so no retry could collide on it; now
            // because the lookup at the top of the delegate finds that row under
            // the payment lock before an insert is attempted. Recovery that
            // cannot run reads as a case handled, so it is gone rather than kept
            // as a backstop. It would also have been broken: the violation
            // aborts this explicit transaction, and the read-back it issued next
            // would have failed with 25P02.
            if (violation.ConstraintName != ActiveTransactionIndex)
            {
                throw;
            }

            throw new TransactionAlreadyInProgressException(request.BookingId);
        }

        return BuildResponse(transaction);
    }

    // The index name from the migration that created it. A literal, because the
    // catch above has to compare against it and EF exposes no strongly-typed
    // handle on a constraint name.
    private const string ActiveTransactionIndex = "ix_transactions_booking_id_active";

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
