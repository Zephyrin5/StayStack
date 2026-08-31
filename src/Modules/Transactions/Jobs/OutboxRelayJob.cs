using Transactions.Outbox;
using TickerQ.Utilities.Base;
namespace Transactions.Jobs;

/// <summary>
///     Backstop for MarkTransactionSucceededHandler's outbox row - see
///     Bookings.Jobs.OutboxRelayJob's own doc comment for the shape this
///     mirrors, and docs/adr/0003 for why.
/// </summary>
public class OutboxRelayJob(TransactionsOutboxDispatcher dispatcher)
{
    private const int BatchSize = 50;
    private static readonly TimeSpan DeadLetterSweepCooldown = TimeSpan.FromHours(1);

    [TickerFunction(functionName: "Transactions.OutboxRelay", cronExpression: "* * * * *")]
    public Task RelayAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.DispatchPendingAsync(BatchSize, cancellationToken);

    // Closes the money-touching dead-letter gap ADR-0003 flags for
    // ConfirmBookingPaymentOutboxMessage - see Bookings.Jobs.OutboxRelayJob's
    // own SweepDeadLetteredAsync TickerFunction for why this shape.
    [TickerFunction(functionName: "Transactions.OutboxDeadLetterSweep", cronExpression: "0 * * * *")]
    public Task SweepDeadLetteredAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.SweepDeadLetteredAsync(BatchSize, DeadLetterSweepCooldown, cancellationToken);
}
