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
///         This is what makes the refund independent of message ordering, and
///         it is the reason the obligation exists as a row rather than as a
///         conditional. The outbox messages that usually trigger resolution are
///         latency optimisations over this sweep - the same relationship
///         PendingBookingIntent has with its reconciler (docs/adr/0017) - so
///         every interleaving that previously ended with both paths declining
///         now ends here instead.
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

    [TickerFunction(functionName: "Bookings.ResolveOutstandingRefunds", cronExpression: "*/5 * * * *")]
    public async Task ResolveAsync(TickerFunctionContext context, CancellationToken cancellationToken)
    {
        DateTimeOffset cutoff = timeProvider.GetUtcNow() - ResolutionGrace;

        List<Guid> candidates = await dbContext.RefundObligations.AsNoTracking()
            .Where(o => o.ResolvedAt == null && o.CancelledAt <= cutoff)
            .OrderBy(o => o.CancelledAt)
            .Take(MaxResultsPerRun)
            .Select(o => o.BookingId)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return;
        }

        if (candidates.Count == MaxResultsPerRun)
        {
            LogResultsCapped(logger, MaxResultsPerRun);
        }

        foreach (Guid bookingId in candidates)
        {
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
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogResolveFailed(logger, bookingId, ex);
            }
        }
    }

    [LoggerMessage(LogLevel.Warning,
        "Refund obligation for booking {BookingId} was settled by the backstop sweep at {Amount}, not by its outbox message - a steady stream of these means outbox delivery is failing")]
    private static partial void LogResolvedByTheBackstop(ILogger logger, Guid bookingId, decimal amount);

    [LoggerMessage(LogLevel.Error,
        "Failed to resolve the refund obligation for booking {BookingId}; the batch continued and the next run will retry it. A row failing every run is money owed and needs a look")]
    private static partial void LogResolveFailed(ILogger logger, Guid bookingId, Exception exception);

    [LoggerMessage(LogLevel.Warning,
        "ResolveOutstandingRefunds hit its per-run cap of {MaxResultsPerRun} obligations - cancellations may be arriving faster than refunds are being settled")]
    private static partial void LogResultsCapped(ILogger logger, int maxResultsPerRun);
}
