using Bookings.Contracts;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Mediator;
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

        Transaction transaction = Transaction.Create(request.BookingId, booking.TotalPrice);

        dbContext.Transactions.Add(transaction);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
            when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation)
        {
            // By constraint name, never by SqlState alone. Two unique indexes
            // reach this catch and they mean opposite things, so the broad
            // version turned a recoverable duplicate into a false conflict.
            //
            // ix_transactions_booking_id_active is the real conflict: another
            // transaction for this booking is already Pending or Succeeded. The
            // pre-check above catches the ordinary case; this catches two
            // concurrent requests that both passed it.
            if (violation.ConstraintName == ActiveTransactionIndex)
            {
                throw new TransactionAlreadyInProgressException(request.BookingId);
            }

            // The primary key, which means this insert already committed and
            // lost its acknowledgement. SaveChangesAsync runs under a retrying
            // execution strategy even without an explicit delegate here, and
            // Transaction.Create ran once - so the retry re-inserts the same
            // id rather than a new one.
            //
            // Reported as "already in progress", that was true and useless: the
            // guest cannot learn the id of the transaction they just created,
            // so the payment is stranded behind a number nobody can see. Read
            // it back and answer with it, the same shape ConfirmBookingHandler
            // uses for its own pre-generated booking id (docs/adr/0025).
            // Anything else is a unique index nobody anticipated, and guessing
            // it is our own committed insert would report success for a write
            // that never happened. Let it surface.
            if (violation.ConstraintName != PrimaryKey)
            {
                throw;
            }

            dbContext.ChangeTracker.Clear();

            Transaction committed = await dbContext.Transactions.AsNoTracking()
                .SingleAsync(t => t.Id == transaction.Id, cancellationToken);

            return BuildResponse(committed);
        }

        return BuildResponse(transaction);
    }

    // The primary key name from the Initial migration. A literal, because the
    // catch above has to compare against it and EF exposes no strongly-typed
    // handle on a constraint name.
    private const string PrimaryKey = "pk_transactions";
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
