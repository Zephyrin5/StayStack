using Bookings.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Outbox;
using System.Text.Json;
using Transactions.Entities;
using Transactions.Serialization;
namespace Transactions.Outbox;

public class TransactionsOutboxDispatcher(
    AppTransactionsDbContext dbContext,
    IBookingPaymentConfirmation bookingPaymentConfirmation,
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
    ///     doesn't exist - the realistic way this message type actually
    ///     exhausts its retries, and not a transient condition more waiting
    ///     fixes. Left unresolved, the transaction sits Succeeded and the
    ///     booking sits Pending forever: money taken, nothing sold, with no
    ///     automatic path back. Compensates the same way the inline
    ///     !confirmed branch above does - full refund, since nothing is
    ///     actually being held against a payment that was never turned into
    ///     a real booking.
    ///     <para>
    ///         Critically, this also resolves the message itself
    ///         (ProcessedAt set, DeadLetteredAt cleared) rather than leaving
    ///         it dead-lettered - SweepDeadLetteredAsync doesn't know this
    ///         row was compensated, and would otherwise keep retrying
    ///         ConfirmPaymentAsync on it forever. If that retry ever
    ///         succeeded after the transaction was already marked
    ///         RefundPending here, the booking would end up Confirmed
    ///         against a transaction already flagged for refund - a worse
    ///         inconsistency than the one this exists to close. Compensating
    ///         is a deliberate, one-way decision once made, not a step in an
    ///         ongoing retry loop.
    ///     </para>
    /// </summary>
    protected override async Task OnDeadLetteredAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        if (message.Type != nameof(ConfirmBookingPaymentOutboxMessage))
        {
            return;
        }

        ConfirmBookingPaymentOutboxMessage payload = DeserializeConfirmBookingPayment(message);
        await MarkRefundPendingIfStillSucceededAsync(payload.TransactionId, cancellationToken);

        message.ProcessedAt = message.DeadLetteredAt;
        message.DeadLetteredAt = null;
    }

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
