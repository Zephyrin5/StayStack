using BuildingBlocks.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TickerQ.Utilities.Base;
using TickerQ.Utilities.Models;
using System.Data;
using Bookings.Contracts;
namespace Bookings.Jobs;

/// <summary>
///     Resolves refund obligations nobody has settled yet.
///     <para>
///         A cancellation resolves its obligation in the scope that commits
///         it, and a payment succeeding afterwards resolves it in the payment's.
///         What reaches this sweep is an obligation with no payment yet, waiting
///         for a late one, or one those resolutions did not record
///         (docs/adr/0027).
///     </para>
///     <para>
///         Lives in Bookings because the obligations are Bookings' rows. The payment behind one is
///         reached through IPaymentReversal, the port the cancellation path already uses.
///     </para>
/// </summary>
public partial class ResolveOutstandingRefundsJob(
    BookingsDb dbContext,
    ITransactionRunner transactionRunner,
    IPaymentReversal paymentReversal,
    IOptions<BookingLifecyclePolicyOptions> policy,
    TimeProvider timeProvider,
    ILogger<ResolveOutstandingRefundsJob> logger)
{
    // Same cap and same reasoning as the sibling sweeps: bounds one run, and
    // costs nothing in recovery because an unresolved obligation stays
    // unresolved and is found again next run.
    private const int MaxResultsPerRun = 1000;

    /// <summary>
    ///     Keeps the sweep off obligations committed moments ago.
    ///     <para>
    ///         Not a correctness bound. Resolution is idempotent, so running
    ///         early would repeat work already committed; the grace exists to
    ///         keep the sweep off fresh rows, not to give anything permission to
    ///         be slow.
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
        // Only obligations a payment can still change. Every other unresolved row was settled at
        // cancellation, expiry or payment failure (docs/adr/0027), so what is left here is genuinely
        // waiting on money: a payment still Pending, or one whose refund was recorded in a commit that
        // did not reach the obligation's marker. Composed rather than fetched - one statement.
        IQueryable<Guid> unsettled = paymentReversal.BookingIdsWithAnUnsettledPayment();

        var candidates = await dbContext.RefundObligations.AsNoTracking()
            .Where(o => o.ResolvedAt == null && o.NextAttemptAt <= cutoff && unsettled.Contains(o.BookingId))
            .OrderBy(o => o.NextAttemptAt)
            .Take(MaxResultsPerRun)
            .Select(o => new { o.BookingId, o.Attempts })
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return;
        }

        // A row that has been looked at this many times is waiting on a payment that is not coming:
        // not an error, and not something the sweep can fix, but the one signal that says so.
        int stalled = candidates.Count(o => o.Attempts >= policy.Value.RefundSweepStalledAfterAttempts);

        if (stalled > 0)
        {
            LogStalledObligations(logger, stalled, policy.Value.RefundSweepStalledAfterAttempts);
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

        foreach (var obligation in candidates)
        {
            Guid bookingId = obligation.BookingId;

            // Per obligation, so one failing row does not strand every refund
            // behind it - and cancellation is deliberately not swallowed, the
            // same split every other sweep in this module makes.
            try
            {
                // The refund and the obligation's ResolvedAt commit together.
                // ResolvedAt means the decision is recorded locally, not that money
                // moved. A payment-provider call must never run inside this scope:
                // it cannot roll back, and it would hold the connection, the row
                // locks and a pool slot for the length of an HTTP round trip. It
                // belongs after the commit, driven by the recorded RefundPending.
                decimal? resolved = await transactionRunner.ExecuteAsync(
                IsolationLevel.ReadCommitted,
                    token => paymentReversal.ResolveRefundAsync(bookingId, token),
                    cancellationToken);

                if (resolved is not null)
                {
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
                await BackOffAsync(bookingId, obligation.Attempts + 1, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Backed off on failure too, for the same reason: a row that
                // throws every run would otherwise hold its place and starve
                // everything behind it, which is how a per-item guard turns
                // into a queue-wide outage.
                LogResolveFailed(logger, bookingId, ex);
                await BackOffAsync(bookingId, obligation.Attempts + 1, cancellationToken);
            }
        }
    }

    // A scheduling hint, not the refund itself, so losing one to a failure costs
    // a repeated sweep rather than anything a guest can see. Filtered on
    // still-unresolved, so it never touches an obligation resolved meanwhile.
    private Task BackOffAsync(Guid bookingId, int attempts, CancellationToken cancellationToken) =>
        dbContext.RefundObligations
            .Where(o => o.BookingId == bookingId && o.ResolvedAt == null)
            .ExecuteUpdateAsync(o => o
                    .SetProperty(row => row.Attempts, attempts)
                    .SetProperty(row => row.NextAttemptAt, timeProvider.GetUtcNow() + BackoffFor(attempts)),
                cancellationToken);

    private static TimeSpan BackoffFor(int attempts)
    {
        // Doubling, capped. Shifting by a bounded exponent rather than Math.Pow
        // so a long-lived row cannot overflow its way to a negative delay.
        double minutes = InitialBackoff.TotalMinutes * Math.Pow(2, Math.Min(attempts - 1, 10));

        return minutes >= MaxBackoff.TotalMinutes ? MaxBackoff : TimeSpan.FromMinutes(minutes);
    }

    [LoggerMessage(LogLevel.Warning,
        "Refund obligation for booking {BookingId} was settled by the backstop sweep at {Amount} - cancellation and payment success resolve inline, so neither recorded it")]
    private static partial void LogResolvedByTheBackstop(ILogger logger, Guid bookingId, decimal amount);

    [LoggerMessage(LogLevel.Error,
        "Failed to resolve the refund obligation for booking {BookingId}; the batch continued and the next run will retry it. A row failing every run is money owed and needs a look")]
    private static partial void LogResolveFailed(ILogger logger, Guid bookingId, Exception exception);

    [LoggerMessage(LogLevel.Warning,
        "{Count} refund obligations have been swept {Threshold} times or more without settling. Each is waiting on a payment that has neither succeeded nor failed, which the sweep cannot resolve on its own")]
    private static partial void LogStalledObligations(ILogger logger, int count, int threshold);

    [LoggerMessage(LogLevel.Warning,
        "ResolveOutstandingRefunds hit its per-run cap of {MaxResultsPerRun} obligations, the most-retried at {MaxAttempts} attempts. A high attempt count means rows waiting on payments that may never arrive; a low one means refunds are genuinely falling behind")]
    private static partial void LogResultsCapped(ILogger logger, int maxResultsPerRun, int maxAttempts);
}
