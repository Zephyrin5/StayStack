using Bookings.Contracts;
using SeedWork.Enums;
namespace Bookings.Entities;

/// <summary>
///     A durable record that a cancelled booking owes a refund, written in the
///     same transaction as the cancellation itself.
///     <para>
///         A persistence-layer construct, not a Domain aggregate: the row is the work item.
///         Cancellation, expiry, payment success and payment failure each resolve it in their own
///         transaction; ResolveOutstandingRefundsJob sweeps what is left.
///     </para>
///     <para>
///         The cancellation decides nothing about payments. It records that a
///         refund is owed and the policy figure; a single resolver decides the
///         amount later, when both the payment and the cancellation are
///         committed facts (docs/adr/0027).
///     </para>
/// </summary>
public sealed class RefundObligation
{
    /// <summary>
    ///     The primary key, and deliberately not a surrogate. One obligation
    ///     per booking is the invariant, and making it the key means a second
    ///     writer collides rather than racing - there is no interleaving that
    ///     produces two.
    /// </summary>
    public Guid BookingId { get; set; }

    /// <summary>
    ///     When the booking was cancelled. Committed with the cancellation, so
    ///     a reader either sees the obligation with a true timestamp or sees no
    ///     obligation at all - never a null standing in for "not yet".
    /// </summary>
    public DateTimeOffset CancelledAt { get; set; }

    /// <summary>
    ///     What the cancellation policy resolved to at cancellation time.
    ///     Carried here because it is only computable on this side, and the
    ///     resolver needs it without having to re-derive a policy against a
    ///     date that has since moved.
    /// </summary>
    public decimal PolicyRefundAmount { get; set; }

    public Currency Currency { get; set; }

    public BookingCancellationCause Cause { get; set; }

    /// <summary>
    ///     Set once a refund has been decided and recorded against the
    ///     transaction. The idempotency marker for a two-commit resolution: a
    ///     crash between writing the refund and marking this leaves the
    ///     obligation unresolved, the backstop job retries, and
    ///     Transaction.MarkRefundPending's own first-wins guard makes the
    ///     repeat a no-op.
    /// </summary>
    public DateTimeOffset? ResolvedAt { get; set; }

    /// <summary>
    ///     How it ended, set with <see cref="ResolvedAt"/> and never without it. Distinguishes a refund
    ///     that was recorded from an obligation nothing was owed against, which the sweep would
    ///     otherwise keep re-examining forever.
    /// </summary>
    public RefundObligationOutcome? Outcome { get; set; }

    /// <summary>
    ///     When the sweep should next consider this row.
    ///     <para>
    ///         Without it the sweep starved under its ordinary workload rather than under any error:
    ///         ordered by CancelledAt, a backlog of rows with nothing to do pinned the window and no
    ///         newer obligation with a payment behind it was ever reached. A row that is waiting for a
    ///         payment is not an error to retry, so it backs off rather than being resolved to clear
    ///         it.
    ///     </para>
    /// </summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>
    ///     How many times the sweep has looked at this row and found nothing to
    ///     do. Turns the per-run cap log into a real signal: a batch of
    ///     high-attempt rows means "waiting on payments that may never come",
    ///     which is a different operational problem from "behind on work".
    /// </summary>
    public int Attempts { get; set; }
}
