using Bookings.Contracts;
using SeedWork.Enums;
namespace Bookings.Entities;

/// <summary>
///     A durable record that a cancelled booking owes a refund, written in the
///     same transaction as the cancellation itself.
///     <para>
///         Persistence-layer construct, not a Domain aggregate - the same
///         reasoning as PendingBookingIntent, and the same relationship to its
///         resolver as an intent has to its reconciler: the row is the work
///         item, and the outbox message that usually carries it is a latency
///         optimisation over the job that sweeps for unresolved ones.
///     </para>
///     <para>
///         <b>Why this exists rather than a fourth conditional.</b> Deciding
///         the amount used to be split across two paths, each inferring that
///         the other would handle the cases it declined. Neither could verify
///         that inference at the moment it was made, and it failed three ways:
///         both paths declining and nobody refunding; the cancellation moment
///         read before it was committed, so a full refund landed where the
///         policy said otherwise; and the expiry job, which cancelled bookings
///         while enqueueing no reversal at all. One conditional per round.
///     </para>
///     <para>
///         The cancellation no longer decides anything about payments. It
///         records that a refund is owed and what the policy figure is; a
///         single resolver decides the amount later, at a point where both the
///         payment and the cancellation are committed facts rather than
///         predictions about each other.
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
}
