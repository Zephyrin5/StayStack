using Bookings.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TickerQ.Utilities.Base;
using TickerQ.Utilities.Models;
using Transactions.Contracts;
namespace Bookings.Jobs;

/// <summary>
///     Resolves refund obligations nobody has settled yet.
///     <para>
///         This makes the refund independent of message ordering. The outbox
///         messages that usually trigger resolution are latency optimisations
///         over this sweep, so any interleaving in which no message resolves an
///         obligation ends here (docs/adr/0027).
///     </para>
///     <para>
///         Lives in Bookings because the obligations are Bookings' rows. It
///         calls into Transactions through ITransactionReversal, which this
///         module already depends on for the cancellation path.
///     </para>
/// </summary>
public partial class ResolveOutstandingRefundsJob(
    AppBookingsDbContext dbContext,
    ITransactionReversal transactionReversal,
    TimeProvider timeProvider,
    ILogger<ResolveOutstandingRefundsJob> logger)
{
    // Same cap and same reasoning as the sibling sweeps: bounds one run, and
    // costs nothing in recovery because an unresolved obligation stays
    // unresolved and is found again next run.
    private const int MaxResultsPerRun = 1000;

    /// <summary>
    ///     Long enough that the ordinary path - an outbox message delivered
    ///     within seconds - has already resolved the obligation, so this job
    ///     normally finds nothing.
    ///     <para>
    ///         Not a correctness bound. Resolution is idempotent and this
    ///         running early would simply do the work the message was about to
    ///         do; the grace exists to keep the sweep off rows that are being
    ///         handled, not to give anything permission to be slow.
    ///     </para>
    /// </summary>
    private static readonly TimeSpan ResolutionGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    ///     How far out a row is pushed after a run that changed nothing,
    ///     doubling per attempt up to <see cref="MaxBackoff"/>.
    ///     <para>
    ///         An obligation with no payment behind it is not an error being
    ///         retried - it is a row waiting for a payment that may never
    ///         arrive. Backing off is what stops the ordinary case, which is
    ///         most cancellations, from filling the window and starving the
    ///         rows that do have money against them.
    ///     </para>
    /// </summary>
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(6);

    [TickerFunction(functionName: "Bookings.ResolveOutstandingRefunds", cronExpression: "*/5 * * * *")]
    public async Task ResolveAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = timeProvider.GetUtcNow() - ResolutionGrace;

        // By NextAttemptAt, not CancelledAt. Ordering by cancellation time let
        // a backlog of unpaid obligations - which resolve to nothing and stay
        // unresolved by design - hold the front of the queue forever, so a
        // newer obligation with an actual payment was never reached. That is
        // the ordinary workload, not an error condition: most cancellations are
        // of bookings nobody paid for.
        List<RefundObligation> candidates = await dbContext.RefundObligations
            .Where(o => o.ResolvedAt == null && o.NextAttemptAt <= cutoff)
            .OrderBy(o => o.NextAttemptAt)
            .Take(MaxResultsPerRun)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return;
        }

        if (candidates.Count == MaxResultsPerRun)
        {
            // Attempts is what makes this log readable. A capped batch of
            // high-attempt rows means "waiting on payments that may never
            // come"; a capped batch of fresh ones means genuinely behind on
            // work. Without it the two were indistinguishable and the warning
            // fired every run forever.
            LogResultsCapped(logger, MaxResultsPerRun, candidates.Max(o => o.Attempts));
        }

        foreach (RefundObligation obligation in candidates)
        {
            Guid bookingId = obligation.BookingId;

            // Per obligation, so one failing row does not strand every refund
            // behind it - and cancellation is deliberately not swallowed, the
            // same split every other sweep in this module makes.
            try
            {
                decimal? resolved = await transactionReversal.ResolveRefundAsync(bookingId, cancellationToken);

                if (resolved is not null)
                {
                    // Worth a Warning rather than Information: the messages
                    // should have handled this, so a steady stream here means
                    // outbox delivery is not working, and the only visible
                    // symptom would otherwise be refunds arriving minutes late.
                    LogResolvedByTheBackstop(logger, bookingId, resolved.Value);
                    continue;
                }

                // Nothing to do - almost always a cancellation with no payment
                // behind it. Backed off rather than left due, so it stops
                // occupying a place in every future batch.
                //
                // Deliberately NOT resolved to clear it. A late payment is
                // still possible, and that is precisely the case this row
                // exists to catch; marking it settled would throw the case away
                // to tidy the queue.
                obligation.Attempts++;
                obligation.NextAttemptAt = timeProvider.GetUtcNow() + BackoffFor(obligation.Attempts);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Backed off on failure too, for the same reason: a row that
                // throws every run would otherwise hold its place and starve
                // everything behind it, which is how a per-item guard turns
                // into a queue-wide outage.
                obligation.Attempts++;
                obligation.NextAttemptAt = timeProvider.GetUtcNow() + BackoffFor(obligation.Attempts);

                LogResolveFailed(logger, bookingId, ex);
            }
        }

        // One save for the whole batch. These are scheduling hints, not the
        // refund itself, so losing them to a failure costs a repeated sweep
        // rather than anything a guest can see.
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static TimeSpan BackoffFor(int attempts)
    {
        // Doubling, capped. Shifting by a bounded exponent rather than Math.Pow
        // so a long-lived row cannot overflow its way to a negative delay.
        double minutes = InitialBackoff.TotalMinutes * Math.Pow(2, Math.Min(attempts - 1, 10));

        return minutes >= MaxBackoff.TotalMinutes ? MaxBackoff : TimeSpan.FromMinutes(minutes);
    }

    [LoggerMessage(LogLevel.Warning,
        "Refund obligation for booking {BookingId} was settled by the backstop sweep at {Amount}, not by its outbox message - a steady stream of these means outbox delivery is failing")]
    private static partial void LogResolvedByTheBackstop(ILogger logger, Guid bookingId, decimal amount);

    [LoggerMessage(LogLevel.Error,
        "Failed to resolve the refund obligation for booking {BookingId}; the batch continued and the next run will retry it. A row failing every run is money owed and needs a look")]
    private static partial void LogResolveFailed(ILogger logger, Guid bookingId, Exception exception);

    [LoggerMessage(LogLevel.Warning,
        "ResolveOutstandingRefunds hit its per-run cap of {MaxResultsPerRun} obligations, the most-retried at {MaxAttempts} attempts. A high attempt count means rows waiting on payments that may never arrive; a low one means refunds are genuinely falling behind")]
    private static partial void LogResultsCapped(ILogger logger, int maxResultsPerRun, int maxAttempts);
}
