using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Features.Common;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using BuildingBlocks.Persistence;
using BuildingBlocks.Time;
using Dapper;
using Mediator;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Promotions.Contracts;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Data.Common;
using System.Data;
namespace Bookings.Features.CancelBooking;

public class CancelBookingHandler(
    BookingsDb dbContext,
    ITransactionRunner transactionRunner,
    IHoldConfirmation holdConfirmation,
    IPromotionRedemption promotionRedemption,
    IPaymentReversal paymentReversal,
    ICurrentUserProvider currentUserProvider,
    IBookingSessions bookingSessions,
    TimeProvider timeProvider) : IRequestHandler<CancelBookingRequest, CancelBookingResponse>
{
    public async ValueTask<CancelBookingResponse> Handle(CancelBookingRequest request, CancellationToken cancellationToken)
    {
        // "Does not exist" and "is not yours" are indistinguishable; see BookingAccessChecker.
        BookingAccess access = await BookingAccessChecker.ResolveAsync(
                                   dbContext, request.BookingId, currentUserProvider.UserId,
                                   await bookingSessions.GetSessionBookingIdAsync(cancellationToken), cancellationToken)
                               ?? throw new NotFoundException(nameof(Booking), request.BookingId);

        Booking booking = access.Booking;

        // Refund tiers count days to a property-local check-in (docs/adr/0018).
        DateOnly today = PropertyTimeZone.Today(timeProvider, booking.TimeZoneId);
        CancellationPolicy cancellationPolicy = booking.CancellationPolicy;

        // A re-cancel writes nothing and only reports.
        if (booking.BookingStatus != BookingStatus.Cancelled)
        {
            if (!booking.CanBeCancelledOn(today))
            {
                throw new BookingNotCancellableException(booking.Id);
            }

            // After eligibility, so a refused or no-op request is not asked for the email.
            RequireGuestEmailForLinkAccess(access, request.GuestEmail);

            // Once, outside the retry, so attempts either side of a tier boundary agree.
            Money refundAmount = CancellationRefund.Compute(booking.TotalPrice, cancellationPolicy, booking.CheckIn, today);

            Booking cancelled = await transactionRunner.ExecuteAsync(
                IsolationLevel.ReadCommitted,
                async token =>
                {
                    DbTransaction transaction = dbContext.Database.CurrentTransaction!.GetDbTransaction();
                    DbConnection connection = dbContext.Database.GetDbConnection();

                    // Advisory lock, booking row, hold, transaction row (docs/adr/0028).
                    BookingPaymentLockHandle heldLock = await BookingPaymentLock.AcquireAsync(
                        connection, transaction, request.BookingId, token);

                    await BookingRowClaim.ClaimAsync(connection, transaction, heldLock, token);

                    Booking locked = await dbContext.Bookings.SingleOrDefaultAsync(b => b.Id == request.BookingId, token)
                                     ?? throw new NotFoundException(nameof(Booking), request.BookingId);

                    // Already cancelled under the lock: this request's earlier attempt committed (docs/adr/0025).
                    if (locked.BookingStatus == BookingStatus.Cancelled)
                    {
                        return locked;
                    }

                    if (!locked.CanBeCancelledOn(today))
                    {
                        throw new BookingNotCancellableException(locked.Id);
                    }

                    DateTimeOffset cancelledAt = timeProvider.GetUtcNow();
                    locked.Cancel(cancelledAt, heldLock);

                    dbContext.RefundObligations.Add(new RefundObligation
                    {
                        BookingId = locked.Id,
                        CancelledAt = cancelledAt,
                        PolicyRefundAmount = refundAmount.Amount,
                        Currency = refundAmount.Currency,
                        Cause = BookingCancellationCause.GuestCancellation,
                        NextAttemptAt = cancelledAt
                    });

                    await holdConfirmation.ReleaseHoldAsync(locked.HoldId, token);
                    await dbContext.SaveChangesAsync(token);
                    await promotionRedemption.ReverseRedemptionAsync(locked.Id, token);

                    // Records the refund decision locally; no provider call may run inside the scope (docs/adr/0027).
                    await paymentReversal.ResolveRefundAsync(locked.Id, token);

                    return locked;
                },
                cancellationToken);

            return await BuildResponseAsync(cancelled, cancellationToken);
        }

        return await BuildResponseAsync(booking, cancellationToken);
    }

    // One read of the payment, so the report cannot straddle a Succeeded -> RefundPending transition.
    private async Task<CancelBookingResponse> BuildResponseAsync(Booking booking, CancellationToken cancellationToken)
    {
        PaymentStateSnapshot? payment = await paymentReversal.GetPaymentStateAsync(booking.Id, cancellationToken);

        if (payment?.RefundAmount is { } recorded)
        {
            return BuildResponse(booking, recorded.Amount, recorded.Currency, PercentOf(recorded, payment.Amount), payment.RefundStatus);
        }

        if (payment is not { AwaitingRefund: true })
        {
            return BuildResponse(booking, refundAmount: null, currency: null, refundPercent: null, RefundStatus.None);
        }

        return await BuildPendingRefundResponseAsync(booking, payment, cancellationToken);
    }

    // A refund owed and not yet recorded, at the amount the resolver will record (docs/adr/0027).
    private async Task<CancelBookingResponse> BuildPendingRefundResponseAsync(
        Booking booking, PaymentStateSnapshot payment, CancellationToken cancellationToken)
    {
        RefundObligation? obligation = await dbContext.RefundObligations.AsNoTracking()
            .SingleOrDefaultAsync(o => o.BookingId == booking.Id, cancellationToken);

        // No obligation: nothing cancelled explains the payment, so it is refunded in full.
        Money amount = obligation is null
            ? payment.Amount
            : RefundDecision.For(
                payment.Amount,
                // AwaitingRefund means the payment succeeded, so this is set.
                payment.SucceededAt ?? throw new InvalidOperationException(
                    $"Booking {booking.Id} has a payment awaiting a refund with no SucceededAt."),
                obligation.CancelledAt,
                Money.Of(obligation.PolicyRefundAmount, obligation.Currency)).Amount;

        return BuildResponse(booking, amount.Amount, amount.Currency, PercentOf(amount, payment.Amount), RefundStatus.Pending);
    }

    private static decimal? PercentOf(Money refund, Money paid) =>
        paid.Amount == 0m ? null : refund.Amount / paid.Amount * 100m;

    // The second factor for management-link access; see CancelBookingRequest.GuestEmail.
    private static void RequireGuestEmailForLinkAccess(BookingAccess access, string? supplied)
    {
        if (access.Kind != BookingAccessKind.Link || CancellationGuestEmail.Matches(supplied, access.Booking.GuestEmail))
        {
            return;
        }

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
