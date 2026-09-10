using Bookings.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Outbox;
using System.Text.Json;
using Transactions.Entities;
using Transactions.Serialization;
namespace Transactions.Outbox;

public partial class TransactionsOutboxDispatcher(
    AppTransactionsDbContext dbContext,
    IBookingPaymentConfirmation bookingPaymentConfirmation,
    IBookingLookup bookingLookup,
    TimeProvider timeProvider,
    ILogger<TransactionsOutboxDispatcher> logger)
    : OutboxDispatcherBase<AppTransactionsDbContext>(dbContext, timeProvider, logger)
{
    protected override string ModuleName => "Transactions";

    protected override async Task TryHandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case nameof(ConfirmBookingPaymentOutboxMessage):
            {
                ConfirmBookingPaymentOutboxMessage payload = DeserializeConfirmBookingPayment(message);

                bool confirmed = await bookingPaymentConfirmation.ConfirmPaymentAsync(payload.BookingId, cancellationToken);

                if (!confirmed)
                {
                    // The booking was already cancelled by the time this
                    // payment resolved - same reasoning as
                    // MarkTransactionSucceededHandler's original inline
                    // branch, moved here since it now runs after the outbox
                    // dispatch rather than inline in the handler.
                    await MarkRefundPendingIfStillSucceededAsync(payload.TransactionId, cancellationToken);
                }

                break;
            }

            default:
                throw new InvalidOperationException($"Unknown Transactions outbox message type '{message.Type}'.");
        }
    }

    /// <summary>
    ///     ConfirmPaymentAsync throws NotFoundException if the booking
    ///     doesn't exist - the realistic way this message type exhausts its
    ///     retries, not a transient condition more waiting fixes. Left
    ///     unresolved, the transaction sits Succeeded and the booking sits
    ///     Pending forever: money taken, nothing sold. Compensates the same
    ///     way the inline !confirmed branch above does - full refund, since
    ///     nothing is held against a payment that was never turned into a
    ///     real booking.
    ///     <para>
    ///         Critically, this also resolves the message itself (ProcessedAt
    ///         set, DeadLetteredAt cleared) rather than leaving it
    ///         dead-lettered - SweepDeadLetteredAsync doesn't know this row
    ///         was compensated, and would otherwise keep retrying
    ///         ConfirmPaymentAsync forever. If that retry ever succeeded
    ///         after the transaction was already marked RefundPending here,
    ///         the booking would end up Confirmed against a transaction
    ///         already flagged for refund - worse than the inconsistency
    ///         this closes. Compensating is a one-way decision once made,
    ///         not a step in an ongoing retry loop.
    ///     </para>
    /// </summary>
    protected override async Task OnDeadLetteredAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.Type != nameof(ConfirmBookingPaymentOutboxMessage))
        {
            return;
        }

        ConfirmBookingPaymentOutboxMessage payload = DeserializeConfirmBookingPayment(message);

        // Ask the booking what actually happened before refunding anything.
        //
        // This used to look only at the transaction: still Succeeded meant
        // refund. But every failed attempt on the way here ran
        // ConfirmPaymentAsync, and the reason a message exhausts its retries
        // is not necessarily that the work failed - a confirmation that
        // commits and then loses its acknowledgement fails identically from
        // out here, and re-running it just confirms an already-Confirmed
        // booking again. Refunding on that evidence takes a stay away from a
        // guest who paid for it and keeps it, since the booking stays
        // Confirmed while the money goes back.
        //
        // Same principle as ConfirmBookingHandler's own catch: ask the
        // database what committed, never infer it from how the call ended.
        BookingAccessResult? booking = await bookingLookup.GetBookingDetailsAsync(payload.BookingId, cancellationToken);

        if (booking is { IsConfirmed: true })
        {
            // The confirmation landed after all. There is nothing to
            // compensate, and the message has done its job - resolve it
            // rather than leaving it dead-lettered for a sweep to retry
            // forever.
            LogConfirmationAlreadyLanded(logger, payload.BookingId, payload.TransactionId);

            message.ProcessedAt = message.DeadLetteredAt;
            message.DeadLetteredAt = null;
            return;
        }

        // Not confirmed, or gone entirely: the payment bought nothing, which
        // is the case this compensation was written for.
        await MarkRefundPendingIfStillSucceededAsync(payload.TransactionId, cancellationToken);

        message.ProcessedAt = message.DeadLetteredAt;
        message.DeadLetteredAt = null;
    }

    [LoggerMessage(LogLevel.Warning,
        "Confirmation for booking {BookingId} dead-lettered, but the booking is Confirmed - transaction {TransactionId} is left alone rather than refunded. The acknowledgement was lost, not the work")]
    private static partial void LogConfirmationAlreadyLanded(ILogger logger, Guid bookingId, Guid transactionId);

    private static ConfirmBookingPaymentOutboxMessage DeserializeConfirmBookingPayment(OutboxMessage message) =>
        JsonSerializer.Deserialize(message.Payload, TransactionsJsonSerializerContext.Default.ConfirmBookingPaymentOutboxMessage)
        ?? throw new InvalidOperationException($"Outbox message {message.Id} had a null {nameof(ConfirmBookingPaymentOutboxMessage)} payload.");

    // MarkRefundPending guards TransactionStatus == Succeeded and throws
    // TransactionAlreadyFinalizedException otherwise (it's a one-shot ledger
    // transition, not an idempotent no-op like ReleaseHoldAsync). Checked
    // explicitly here so calling this twice for the same transaction - a
    // retried dispatch, or the !confirmed branch above followed later by
    // OnDeadLetteredAsync racing it - is always a safe no-op past the first
    // time, rather than being mistaken for a real failure.
    private async Task MarkRefundPendingIfStillSucceededAsync(Guid transactionId, CancellationToken cancellationToken)
    {
        Transaction transaction = await DbContext.Transactions
            .SingleAsync(t => t.Id == transactionId, cancellationToken);

        if (transaction.TransactionStatus == TransactionStatus.Succeeded)
        {
            transaction.MarkRefundPending(transaction.Amount);
        }
    }
}
