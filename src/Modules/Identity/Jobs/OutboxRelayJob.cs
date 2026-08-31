using Identity.Outbox;
using TickerQ.Utilities.Base;
namespace Identity.Jobs;

/// <summary>
///     Backstop for BecomeHostHandler's outbox row - see
///     Bookings.Jobs.OutboxRelayJob's own doc comment for the shape this
///     mirrors, and docs/adr/0003 for why.
/// </summary>
public class OutboxRelayJob(IdentityOutboxDispatcher dispatcher)
{
    private const int BatchSize = 50;
    private static readonly TimeSpan DeadLetterSweepCooldown = TimeSpan.FromHours(1);

    [TickerFunction(functionName: "Identity.OutboxRelay", cronExpression: "* * * * *")]
    public Task RelayAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.DispatchPendingAsync(BatchSize, cancellationToken);

    // See Bookings.Jobs.OutboxRelayJob's own SweepDeadLetteredAsync
    // TickerFunction for why this shape.
    [TickerFunction(functionName: "Identity.OutboxDeadLetterSweep", cronExpression: "0 * * * *")]
    public Task SweepDeadLetteredAsync(TickerFunctionContext context, CancellationToken cancellationToken) =>
        dispatcher.SweepDeadLetteredAsync(BatchSize, DeadLetterSweepCooldown, cancellationToken);
}
