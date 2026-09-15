using Bookings.Contracts;
using SeedWork.Enums;
namespace Bookings.Entities;

/// <summary>
///     A durable record that a cancelled booking owes a refund, written in the
///     same transaction as the cancellation itself.
///     <para>
///         Persistence-layer construct, not a Domain aggregate: the row is the
///         work item. A cancellation resolves it in its own scope, and a payment
///         succeeding afterwards resolves it in the payment's;
///         ResolveOutstandingRefundsJob sweeps for any left unresolved.
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
    ///     When the sweep should next consider this row.
    ///     <para>
    ///         Without it the sweep starved under its ordinary workload rather
    ///         than under any error. Both writers record an obligation whether
    ///         or not a payment ever succeeded, and for an unpaid booking - the
    ///         common case by a wide margin - the resolver correctly does
    ///         nothing, so the row stays unresolved and keeps its place at the
    ///         front of an ordering by CancelledAt. A thousand of those pin the
    ///         window permanently and no newer obligation with an actual
    ///         payment behind it is ever reached.
    ///     </para>
    ///     <para>
    ///         Backing off is right rather than merely cheap: an unpaid
    ///         cancellation is not an error to retry, it is a row waiting for a
    ///         payment that may still arrive. Resolving it to clear it would
    ///         throw away exactly the case the obligation exists for.
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
