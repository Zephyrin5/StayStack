using Microsoft.Extensions.Options;
using Bookings.Entities;
using Bookings.Features.Common;
using Bookings.Outbox;
using Dapper;
using System.Data.Common;
using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Bookings.Serialization;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Time;
using Mediator;
using Outbox;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using Transactions.Contracts;
using Bookings.Contracts;
namespace Bookings.Features.CancelBooking;

public class CancelBookingHandler(
    AppBookingsDbContext dbContext,
    BookingsOutboxDispatcher dispatcher,
    IHoldConfirmation holdConfirmation,
    ITransactionReversal transactionReversal,
    ICurrentUserProvider currentUserProvider,
    IBookingSessions bookingSessions,
    TimeProvider timeProvider) : IRequestHandler<CancelBookingRequest, CancelBookingResponse>
{
    public async ValueTask<CancelBookingResponse> Handle(CancelBookingRequest request, CancellationToken cancellationToken)
    {
        // Two proofs of ownership - a matching CustomerId, or a booking session
        // opened from the management token - and no distinction between "does
        // not exist" and "is not yours". See BookingAccessChecker.
        BookingAccess access = await BookingAccessChecker.ResolveAsync(
                              dbContext, request.BookingId, currentUserProvider.UserId,
                              await bookingSessions.GetSessionBookingIdAsync(cancellationToken), cancellationToken)
                          ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        Booking booking = access.Booking;

        // In the property's zone: refund tiers count days to CheckIn, a
        // property-local date, so a UTC "today" crosses tier boundaries a day
        // early or late (docs/adr/0018).
        DateOnly today = PropertyTimeZone.Today(timeProvider, booking.TimeZoneId);

        // A booking without a snapshotted policy gets the default new units get,
        // rather than a claim about what applied when it was made.
        CancellationPolicy cancellationPolicy = booking.CancellationPolicy ?? CancellationPolicy.CreateDefault();

        // Guest policy, used only to fix the refund obligation's amount. Every
        // figure reported afterwards comes from the obligation through
        // RefundDecision (docs/adr/0027); computing policy again at report time
        // would disagree with the refund recorded for expiries and late payments.
        //
        // Cancelling on or after check-in day lands on the strictest tier. The
        // division happens first, in plain decimal: Money rounds on every
        // operation, so `a * b / c` and `a * (b / c)` differ.
        Money ComputeRefund(DateOnly asOf)
        {
            // Computed once, outside the retry: attempts on either side of a tier
            // boundary must not disagree. It reads only fields Cancel() does not
            // change, so the pre-lock snapshot is sufficient.
            int daysBeforeCheckIn = Math.Max(booking.CheckIn.DayNumber - asOf.DayNumber, 0);
            decimal percent = cancellationPolicy.ResolveRefundPercent(daysBeforeCheckIn);
            return booking.TotalPrice * (percent / 100m);
        }

        // A re-cancel writes nothing: the first cancellation's obligation and
        // outbox rows are durable and retried by the relay (docs/adr/0003).
        if (booking.BookingStatus != BookingStatus.Cancelled)
        {
            // Eligibility is separate from access. A management link stays usable
            // long after checkout, and cancelling a stay in progress or already
            // ended would release nights back to inventory. A re-cancel skips
            // this, since it changes nothing.
            if (!booking.CanBeCancelledOn(today))
            {
                throw new BookingNotCancellableException(booking.Id);
            }

            // After the eligibility check, and only on the branch that cancels:
            // asking for the guest's email for a stay that cannot be cancelled, or
            // for a re-cancel that changes nothing, would refuse harmless requests.
            // Checking eligibility first leaks nothing the management response
            // does not already show.
            RequireGuestEmailForLinkAccess(access, request.GuestEmail);

            Money refundAmount = ComputeRefund(today);

            // Everything that must survive a retry is built inside the delegate
            // (docs/adr/0025). SaveChangesAsync accepts its changes before the
            // commit, so a booking mutated and rows enqueued outside it would be
            // seen as unchanged on a retry: the save would write nothing while
            // the hold release, a plain statement, ran again.
            IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

            CancelOutcome outcome = await strategy.ExecuteAsync(async () =>
            {
                dbContext.ChangeTracker.Clear();

                await using IDbContextTransaction transaction =
                    await dbContext.Database.BeginTransactionAsync(cancellationToken);

                // BookingPaymentLock first, then the booking row lock, then the
                // hold (docs/adr/0028). The position is load-bearing:
                // ExpireUnpaidBookingsJob takes the same two locks, and two paths
                // taking them in different orders can deadlock; taken first, a
                // wait on it holds no row lock, so BookingPaymentConfirmation -
                // which takes only the row lock - never queues behind an
                // initiation. The advisory lock exists because initiation, in
                // Transactions, cannot take a row lock on this table.
                if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                {
                    await dbContext.Database.GetDbConnection().ExecuteAsync(new CommandDefinition(
                        AdvisoryLock.AcquireExclusiveSql,
                        new { LockKey = BookingPaymentLock.KeyFor(request.BookingId) },
                        transaction.GetDbTransaction(),
                        cancellationToken: cancellationToken));
                }

                // The booking row before the hold, matching
                // BookingPaymentConfirmation; the reverse order deadlocks against a
                // concurrent payment.
                //
                // The id through Dapper, then the entity through EF. A FromSqlRaw
                // over `SELECT *` fails: EF composes a projection asking for
                // "TotalPrice_Amount", a column the snake_case convention never
                // produces for the complex Money property.
                //
                // FOR UPDATE rather than SKIP LOCKED: a caller is waiting and must
                // see the committed outcome.
                if (dbContext.Database.ProviderName == "Npgsql.EntityFrameworkCore.PostgreSQL")
                {
                    DbConnection connection = dbContext.Database.GetDbConnection();

                    await connection.ExecuteScalarAsync<Guid?>(new CommandDefinition(
                        """SELECT id FROM "bookings" WHERE id = @BookingId FOR UPDATE""",
                        new { request.BookingId },
                        transaction.GetDbTransaction(),
                        cancellationToken: cancellationToken));
                }

                // Re-read under the lock: the instance BookingAccessChecker
                // returned predates the transaction, and on a retry may describe a
                // state a previous attempt changed.
                Booking? locked = await dbContext.Bookings
                    .SingleOrDefaultAsync(b => b.Id == request.BookingId, cancellationToken);

                if (locked is null)
                {
                    throw new NotFoundException(nameof(Booking), request.BookingId);
                }

                // Already cancelled under the lock means an earlier attempt of this
                // request committed and lost its acknowledgement: its obligation and
                // outbox rows are durable, so this is success, not a conflict.
                if (locked.BookingStatus == BookingStatus.Cancelled)
                {
                    await transaction.RollbackAsync(cancellationToken);
                    return new CancelOutcome(locked, null, null);
                }

                if (!locked.CanBeCancelledOn(today))
                {
                    throw new BookingNotCancellableException(locked.Id);
                }

                DateTimeOffset cancelledAt = timeProvider.GetUtcNow();
                locked.Cancel(cancelledAt);

                // The refund obligation, in the cancellation's own transaction, so
                // it is visible if and only if the cancellation committed. It says
                // nothing about whether a payment exists; the resolver decides
                // that (docs/adr/0027).
                dbContext.RefundObligations.Add(new RefundObligation
                {
                    BookingId = locked.Id,
                    CancelledAt = cancelledAt,
                    PolicyRefundAmount = refundAmount.Amount,
                    Currency = refundAmount.Currency,
                    Cause = BookingCancellationCause.GuestCancellation,
                    // Due immediately; the sweep acts only if the inline dispatch
                    // below does not.
                    NextAttemptAt = cancelledAt
                });

                OutboxMessage reverseTransactionRow = dispatcher.Enqueue(
                    // The booking id only. The amount lives on the obligation, and a
                    // second copy here could disagree with it.
                    new ReverseTransactionOutboxMessage(locked.Id),
                    BookingsJsonSerializerContext.Default.ReverseTransactionOutboxMessage);
                OutboxMessage reverseRedemptionRow = dispatcher.Enqueue(
                    new ReverseRedemptionOutboxMessage(locked.Id),
                    BookingsJsonSerializerContext.Default.ReverseRedemptionOutboxMessage);

                // Joins this transaction (HoldConfirmation.AmbientTransaction), so
                // the release commits or rolls back with the cancellation.
                await holdConfirmation.ReleaseHoldAsync(locked.HoldId, cancellationToken);

                await dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                return new CancelOutcome(locked, reverseTransactionRow, reverseRedemptionRow);
            });

            // After the commit, and only for rows this attempt enqueued. A
            // recovered commit's rows belong to its earlier attempt and are
            // already dispatched or waiting for the relay.
            if (outcome.ReverseTransactionRow is not null)
            {
                await dispatcher.TryDispatchAsync(outcome.ReverseTransactionRow, cancellationToken);
            }

            if (outcome.ReverseRedemptionRow is not null)
            {
                await dispatcher.TryDispatchAsync(outcome.ReverseRedemptionRow, cancellationToken);
            }

            // One observation of the payment, from committed state. Two reads could
            // straddle a Succeeded -> RefundPending transition made by a dispatcher
            // or the sweep and describe a state that never existed.
            PaymentStateSnapshot? payment =
                await transactionReversal.GetPaymentStateAsync(outcome.Booking.Id, cancellationToken);

            if (payment?.RefundAmount is { } recorded)
            {
                return BuildResponse(
                    outcome.Booking,
                    recorded.Amount,
                    recorded.Currency,
                    payment.Amount.Amount == 0m ? null : recorded.Amount / payment.Amount.Amount * 100m,
                    payment.RefundStatus);
            }

            if (payment is not { AwaitingRefund: true })
            {
                // No payment: no refund. Reporting the policy figure would promise
                // money to every guest who cancels an unpaid booking.
                return BuildResponse(
                    outcome.Booking, refundAmount: null, currency: null, refundPercent: null, RefundStatus.None);
            }

            return await BuildPendingRefundResponseAsync(outcome.Booking, payment, cancellationToken);
        }

        // A re-cancel reports state settled by an earlier request, from the same
        // single observation.
        PaymentStateSnapshot? paymentState =
            await transactionReversal.GetPaymentStateAsync(booking.Id, cancellationToken);

        if (paymentState?.RefundAmount is { } settledRefund)
        {
            // Transaction.Create forbids a zero amount, but that rule lives in
            // another module and decimal division by zero throws; a null percent
            // beside a real amount is the answer if it ever changes.
            decimal? refundPercent = paymentState.Amount.Amount == 0m
                ? null
                : settledRefund.Amount / paymentState.Amount.Amount * 100m;

            return BuildResponse(
                booking, settledRefund.Amount, settledRefund.Currency,
                refundPercent, paymentState.RefundStatus);
        }

        if (paymentState is not { AwaitingRefund: true })
        {
            return BuildResponse(booking, refundAmount: null, currency: null, refundPercent: null, RefundStatus.None);
        }

        return await BuildPendingRefundResponseAsync(booking, paymentState, cancellationToken);
    }

    /// <summary>
    ///     A refund owed and not yet recorded, reported at the amount the resolver
    ///     will record: RefundDecision over the committed obligation
    ///     (docs/adr/0027). Guest policy enters only as the obligation's amount.
    /// </summary>
    private async Task<CancelBookingResponse> BuildPendingRefundResponseAsync(
        Booking booking, PaymentStateSnapshot payment, CancellationToken cancellationToken)
    {
        RefundObligation? obligation = await dbContext.RefundObligations.AsNoTracking()
            .SingleOrDefaultAsync(o => o.BookingId == booking.Id, cancellationToken);

        // No obligation means no cancellation explains the payment - unreachable
        // for a booking either cancelling path cancelled. Reported as what then
        // happens to it: a payment against a cancelled booking is refunded in full.
        Money amount = obligation is null
            ? payment.Amount
            : RefundDecision.For(
                payment.Amount,
                payment.SucceededAt,
                obligation.CancelledAt,
                Money.Of(obligation.PolicyRefundAmount, obligation.Currency)).Amount;

        // The same ratio the settled branch reports, so a pending figure and the
        // refund it becomes agree on percent too.
        decimal? percent = payment.Amount.Amount == 0m ? null : amount.Amount / payment.Amount.Amount * 100m;

        return BuildResponse(booking, amount.Amount, amount.Currency, percent, RefundStatus.Pending);
    }

    /// <summary>
    ///     What the retried delegate produced: the committed booking, and the rows
    ///     to dispatch - null when this attempt recovered an earlier attempt's
    ///     commit, whose rows are already in flight.
    /// </summary>
    private sealed record CancelOutcome(
        Booking Booking, OutboxMessage? ReverseTransactionRow, OutboxMessage? ReverseRedemptionRow);

    /// <summary>
    ///     The second factor on the destructive action - see
    ///     CancelBookingRequest.GuestEmail for why it exists and why it does
    ///     not apply to account holders.
    /// </summary>
    private static void RequireGuestEmailForLinkAccess(BookingAccess access, string? supplied)
    {
        if (access.Kind != BookingAccessKind.Link)
        {
            return;
        }

        // Case-insensitive and trimmed: rejecting the right address in the wrong
        // case teaches people the field is broken.
        //
        // An ordinary comparison, not a fixed-time one. This confirms the caller
        // knows the booking; the link is the credential, and the endpoint's rate
        // limit bounds guessing.
        if (!string.IsNullOrWhiteSpace(supplied)
            && string.Equals(supplied.Trim(), access.Booking.GuestEmail?.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        // Keyed to the field, and says only that it did not match.
        throw new ValidationException(
            nameof(CancelBookingRequest.GuestEmail),
            "Enter the email address this booking was made with to confirm the cancellation.");
    }

    private static CancelBookingResponse BuildResponse(
        Booking booking,
        decimal? refundAmount,
        Currency? currency,
        decimal? refundPercent,
        RefundStatus refundStatus) =>
        new CancelBookingResponse
        {
            BookingId = booking.Id,
            BookingStatus = booking.BookingStatus,
            RefundAmount = refundAmount,
            Currency = currency,
            RefundPercent = refundPercent,
            RefundStatus = refundStatus
        };
}
