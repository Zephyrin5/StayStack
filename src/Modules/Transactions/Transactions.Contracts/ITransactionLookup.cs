namespace Transactions.Contracts;

/// <summary>
///     Lets Bookings ask whether a booking has actually been paid for, without
///     referencing Transactions' entities or AppTransactionsDbContext directly
///     - same boundary reasoning as Bookings.Contracts.IBookingLookup in the
///     other direction.
///     <para>
///         Separate from <see cref="ITransactionReversal"/> deliberately.
///         That interface exists for a booking somebody is cancelling, and its
///         two reads answer "what refund is owed". This one is asked about a
///         booking that is still alive, before anything has been decided about
///         it, and the two are needed by different callers for opposite
///         reasons.
///     </para>
/// </summary>
public interface ITransactionLookup
{
    /// <summary>
    ///     Whether money has been collected for this booking and not since
    ///     given back - a transaction in <c>Succeeded</c>, the one status that
    ///     means the gateway took the payment and nothing has reversed it.
    ///     <para>
    ///         The authoritative answer to "may this booking be expired",
    ///         because Bookings cannot answer it from its own state.
    ///         MarkTransactionSucceededHandler commits <c>Succeeded</c> and an
    ///         outbox row together and only then dispatches; until that row is
    ///         delivered, a paid booking is still <c>Pending</c> with a
    ///         <c>PaymentDueAt</c> in the past and is indistinguishable from
    ///         an abandoned one. Delivery is not instant - OutboxRelayJob runs
    ///         on a cron and the dead-letter sweep hourly - so that window is
    ///         far wider than any payment deadline.
    ///     </para>
    ///     <para>
    ///         <b>Deliberately unbounded in time.</b> The obvious signature is
    ///         "succeeded before the deadline", so a payment that lands late
    ///         does not rescue a claim that already lapsed. It is the wrong
    ///         shape today, and would be a narrower version of the bug it is
    ///         meant to fix: there is no gateway event time recorded anywhere,
    ///         so the only timestamp available is when <em>this system
    ///         noticed</em>. Comparing that against the deadline cancels and
    ///         refunds payments the customer made in good time and a slow
    ///         webhook reported late - punishing gateway latency, which is
    ///         exactly the failure being closed here.
    ///     </para>
    ///     <para>
    ///         So any succeeded payment blocks expiry, and the cost of that is
    ///         small and lands on the right side: a guest who paid two hours
    ///         late keeps the room they paid for, and the unit stays blocked
    ///         while money is outstanding, which is what being paid means. Add
    ///         the bound when a real provider supplies the moment the customer
    ///         actually paid, store that on Transaction, and compare
    ///         <em>that</em> against PaymentDueAt.
    ///     </para>
    ///     <para>
    ///         False once a refund starts. RefundPending, Refunded and
    ///         RefundFailed are all past Succeeded, so a booking whose payment
    ///         was given back is expirable again - which is correct, since
    ///         nothing is outstanding against it any more.
    ///     </para>
    /// </summary>
    Task<bool> HasSucceededPaymentAsync(Guid bookingId, CancellationToken cancellationToken);
}
