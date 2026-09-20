using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Features.Common;
using BuildingBlocks.Persistence;
using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Promotions.Contracts;
using System.Data.Common;
using TickerQ.Utilities.Base;
using TickerQ.Utilities.Models;
using System.Data;
namespace Bookings.Jobs;

/// <summary>
///     Cancels bookings past <see cref="Booking.PaymentDueAt"/> and releases their holds (docs/adr/0020).
/// </summary>
public partial class ExpireUnpaidBookingsJob(
    BookingsDb dbContext,
    ITransactionRunner transactionRunner,
    IHoldConfirmation holdConfirmation,
    IPromotionRedemption promotionRedemption,
    IPaymentReversal paymentReversal,
    TimeProvider timeProvider,
    ILogger<ExpireUnpaidBookingsJob> logger)
{
    // Bounds one run; an overdue booking is found again next run.
    private const int MaxResultsPerRun = 1000;

    [TickerFunction(functionName: "Bookings.ExpireUnpaidBookings", cronExpression: "* * * * *")]
    public async Task ExpireAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset now = timeProvider.GetUtcNow();

        List<Guid> candidateIds = await dbContext.Bookings.AsNoTracking()
            .Where(b => b.BookingStatus == BookingStatus.Pending
                        && b.PaymentDueAt != null
                        && b.PaymentDueAt <= now)
            .OrderBy(b => b.PaymentDueAt)
            .Take(MaxResultsPerRun)
            .Select(b => b.Id)
            .ToListAsync(cancellationToken);

        if (candidateIds.Count == 0)
        {
            return;
        }

        if (candidateIds.Count == MaxResultsPerRun)
        {
            LogResultsCapped(logger, MaxResultsPerRun);
        }

        foreach (Guid bookingId in candidateIds)
        {
            // Per booking, so one failing row does not strand the rest.
            try
            {
                await ClaimAndExpireAsync(bookingId, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                BookingsTelemetry.UnpaidBookingExpireFailed.Add(1);
                LogExpireFailed(logger, bookingId, ex);
            }
        }
    }

    private async Task ClaimAndExpireAsync(Guid bookingId, CancellationToken cancellationToken)
    {
        bool expired = await transactionRunner.ExecuteAsync(
                IsolationLevel.ReadCommitted,
            async token =>
            {
                DbTransaction transaction = dbContext.Database.CurrentTransaction!.GetDbTransaction();
                DbConnection connection = dbContext.Database.GetDbConnection();

                // Advisory lock, then the row (docs/adr/0028). Both non-blocking: a contended booking waits for the next run.
                BookingPaymentLockHandle? heldLock = await BookingPaymentLock.TryAcquireAsync(
                    connection, transaction, bookingId, token);

                if (heldLock is null || !await BookingRowClaim.TryClaimAsync(connection, transaction, heldLock, token))
                {
                    return false;
                }

                Booking? booking = await dbContext.Bookings.SingleOrDefaultAsync(b => b.Id == bookingId, token);

                // Re-checked under the lock: payment success confirms the booking under this same row lock.
                if (booking is null
                    || booking.BookingStatus != BookingStatus.Pending
                    || booking.PaymentDueAt is null
                    || booking.PaymentDueAt > timeProvider.GetUtcNow())
                {
                    return false;
                }

                await holdConfirmation.ReleaseHoldAsync(booking.HoldId, token);

                DateTimeOffset cancelledAt = timeProvider.GetUtcNow();
                booking.Cancel(cancelledAt, heldLock);

                // At the full price: the platform reclaimed the inventory, the guest did not cancel (docs/adr/0027).
                dbContext.RefundObligations.Add(new RefundObligation
                {
                    BookingId = booking.Id,
                    CancelledAt = cancelledAt,
                    PolicyRefundAmount = booking.TotalPrice.Amount,
                    Currency = booking.TotalPrice.Currency,
                    Cause = BookingCancellationCause.Expiry,
                    NextAttemptAt = cancelledAt
                });
                await dbContext.SaveChangesAsync(token);

                await promotionRedemption.ReverseRedemptionAsync(booking.Id, token);

                // Settles the obligation here rather than leaving it to the sweep. An expiry only fires
                // on an unpaid booking, so the usual outcome is NothingOwed and the row is finished the
                // moment it is written; a payment already Pending leaves it open, which is the case the
                // obligation exists for (docs/adr/0027).
                await paymentReversal.ResolveRefundAsync(booking.Id, token);

                return true;
            },
            cancellationToken);

        // After the scope returns, so a retried attempt counts once.
        if (expired)
        {
            BookingsTelemetry.UnpaidBookingExpired.Add(1);
            LogExpired(logger, bookingId);
        }
    }

    [LoggerMessage(LogLevel.Information,
        "Expired unpaid booking {BookingId}; its hold was released and any promo redemption reversed")]
    private static partial void LogExpired(ILogger logger, Guid bookingId);

    [LoggerMessage(LogLevel.Error,
        "Failed to expire unpaid booking {BookingId}; the batch continued and the next run will retry it. A row failing every run is holding inventory and needs a look")]
    private static partial void LogExpireFailed(ILogger logger, Guid bookingId, Exception exception);

    [LoggerMessage(LogLevel.Warning,
        "ExpireUnpaidBookings hit its per-run cap of {MaxResultsPerRun} bookings - unpaid bookings may be arriving faster than this job clears them")]
    private static partial void LogResultsCapped(ILogger logger, int maxResultsPerRun);
}
