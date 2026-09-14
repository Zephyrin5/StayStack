using SeedWork.ValueObjects;
namespace Bookings.Contracts;

/// <summary>
///     How much a cancelled booking's payment is owed back - the single owner of
///     that decision.
///     <para>
///         TransactionReversal records the refund from it, and CancelBookingHandler
///         reports a pending refund from it; the two must agree exactly, so neither
///         computes the amount any other way. Guest cancellation policy is applied
///         once, when the obligation is written, and reaches this only as the
///         obligation's amount.
///     </para>
///     <para>
///         In Bookings.Contracts because both modules call it: the obligation is
///         Bookings' and the resolver is Transactions'.
///     </para>
/// </summary>
public sealed record RefundDecision(Money Amount, bool PaidAfterTheCancellation)
{
    /// <param name="paymentAmount">What the payment collected.</param>
    /// <param name="succeededAt">
    ///     When it succeeded, or null on a row predating that column.
    /// </param>
    /// <param name="cancelledAt">The obligation's cancellation instant.</param>
    /// <param name="policyRefundAmount">
    ///     The obligation's amount, fixed when it was written - guest policy for
    ///     a guest cancellation, the full price for an expiry.
    /// </param>
    public static RefundDecision For(
        Money paymentAmount, DateTimeOffset? succeededAt, DateTimeOffset cancelledAt, Money policyRefundAmount)
    {
        // Paid, then cancelled: the guest bought a stay and gave it up, so the
        // obligation's amount applies. Cancelled, then paid: that payment
        // bought nothing, so all of it goes back.
        //
        // A null SucceededAt takes the obligation's amount - unknown history,
        // the conservative answer - and never "somebody else owns this", which
        // is the inference that once produced refunds nobody wrote.
        bool paidAfter = succeededAt is { } succeeded && succeeded > cancelledAt;

        return new RefundDecision(paidAfter ? paymentAmount : policyRefundAmount, paidAfter);
    }

    public static RefundDecision For(
        Money paymentAmount, DateTimeOffset? succeededAt, RefundObligationSnapshot obligation) =>
        For(paymentAmount, succeededAt, obligation.CancelledAt, obligation.PolicyRefundAmount);
}
