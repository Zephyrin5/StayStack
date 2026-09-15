using Bookings.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Outbox;
using System.Text.Json;
using Transactions.Contracts;
using Transactions.Entities;
using Transactions.Serialization;
namespace Transactions.Outbox;

public partial class TransactionsOutboxDispatcher(
    AppTransactionsDbContext dbContext,
    IBookingPaymentConfirmation bookingPaymentConfirmation,
    IBookingLookup bookingLookup,
    ITransactionReversal transactionReversal,
    TimeProvider timeProvider,
    ILogger<TransactionsOutboxDispatcher> logger)
    : OutboxDispatcherBase<AppTransactionsDbContext>(dbContext, timeProvider, logger)
{
    protected override string ModuleName => "Transactions";

    protected override async Task TryHandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        switch (message.Type)
        {
            case ConfirmBookingPaymentOutboxMessage.TypeName:
            {
                ConfirmBookingPaymentOutboxMessage payload = DeserializeConfirmBookingPayment(message);

                bool confirmed = await bookingPaymentConfirmation.ConfirmPaymentAsync(payload.BookingId, cancellationToken);

                if (!confirmed)
                {
                    // The booking was already cancelled by the time this
                    // payment resolved.
                    await ResolveRefundAsync(payload.TransactionId, cancellationToken);
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
        if (message.Type != ConfirmBookingPaymentOutboxMessage.TypeName)
        {
            return;
        }

        ConfirmBookingPaymentOutboxMessage payload = DeserializeConfirmBookingPayment(message);

        // Ask the booking what committed before refunding anything. A message
        // exhausts its retries when the work fails, but also when a confirmation
        // commits and loses its acknowledgement every time. Refunding on the
        // transaction's status alone would take back the money for a stay the
        // booking still holds as Confirmed.
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
        await ResolveRefundAsync(payload.TransactionId, cancellationToken);

        message.ProcessedAt = message.DeadLetteredAt;
        message.DeadLetteredAt = null;
    }

    [LoggerMessage(LogLevel.Warning,
        "Confirmation for booking {BookingId} dead-lettered, but the booking is Confirmed - transaction {TransactionId} is left alone rather than refunded. The acknowledgement was lost, not the work")]
    private static partial void LogConfirmationAlreadyLanded(ILogger logger, Guid bookingId, Guid transactionId);

    private static ConfirmBookingPaymentOutboxMessage DeserializeConfirmBookingPayment(OutboxMessage message) =>
        JsonSerializer.Deserialize(message.Payload, TransactionsJsonSerializerContext.Default.ConfirmBookingPaymentOutboxMessage)
        ?? throw new InvalidOperationException($"Outbox message {message.Id} had a null {nameof(ConfirmBookingPaymentOutboxMessage)} payload.");

    /// <summary>
    ///     Asks the one resolver to settle whatever this booking is owed.
    ///     <para>
    ///         This path knows only that the payment resolved against a booking
    ///         that could not use it. Whether that is worth a policy refund or the
    ///         whole amount depends on two committed facts, which the resolver
    ///         reads (docs/adr/0027).
    ///     </para>
    /// </summary>
    private Task ResolveRefundAsync(Guid transactionId, CancellationToken cancellationToken) =>
        // RefundUnusablePaymentAsync, not ResolveRefundAsync: they differ when no
        // obligation exists. This path has established the payment could not
        // become a stay, so a booking with no cancellation behind it - gone, or
        // with its hold lost - is owed the whole amount rather than nothing.
        //
        // By transaction id: the message carries it, and a booking-wide lookup
        // would have to choose between a RefundPending and a Succeeded attempt.
        transactionReversal.RefundUnusablePaymentByTransactionAsync(transactionId, cancellationToken);
}
