using Ardalis.GuardClauses;
using SeedWork.Abstractions;
using SeedWork.Interfaces;
using SeedWork.ValueObjects;
using Transactions.Exceptions;
namespace Transactions.Entities;

public sealed class Transaction : Entity, IAggregateRoot
{
    // EF Core's constructor-binding convention can't bind a parameter typed
    // as a ComplexProperty (Money) back to the entity's own mapped complex
    // property - see Booking's identical constructor pair for the full
    // explanation and docs/adr/0015. EF's materialization fallback only;
    // Create() below still goes through the full validated constructor for
    // every write. No reference-type properties here need a placeholder -
    // FailureReason/RefundAmount are both nullable already.
    private Transaction()
    {
    }

    // See Property.cs (Catalog) for why materialization goes through a
    // real constructor rather than a parameterless one + `required`/`null!`.
    private Transaction(
        Guid id,
        Guid bookingId,
        Money amount,
        TransactionStatus transactionStatus)
    {
        Id = id;
        BookingId = bookingId;
        Amount = amount;
        TransactionStatus = transactionStatus;
    }

    // Cross-module reference, plain Guid rather than a real FK - same
    // pattern as Booking.UnitId (Bookings referencing Catalog). Resolved
    // through Bookings.Contracts, never through a direct reference to
    // Bookings' own entities.
    public Guid BookingId { get; private set; }

    // Snapshotted from the booking at initiation time, not a live read -
    // what was actually charged shouldn't drift if the booking's total
    // changes later. This is where the transaction's one currency lives;
    // RefundAmount below derives from it.
    public Money Amount { get; private set; }

    // Named TransactionStatus, not Status - Status is already claimed by
    // the inherited Entity.Status (EntityStatus: soft-delete state), same
    // reasoning as Booking.BookingStatus.
    public TransactionStatus TransactionStatus { get; private set; }
    public string? FailureReason { get; private set; }

    /// <summary>
    ///     When this payment succeeded, as this system observed it.
    ///     <para>
    ///         Exists to order a payment against a cancellation, which is the
    ///         only thing that can decide which of two refund amounts is owed -
    ///         see <see cref="RefundOwedIsThisPathsToWrite"/>. It was
    ///         deliberately left out when ITransactionLookup was added, on the
    ///         grounds that nothing read it; something reads it now.
    ///     </para>
    ///     <para>
    ///         Local observation time, not provider event time. Nothing
    ///         supplies the latter yet. The distinction matters less here than
    ///         it does for the expiry guard: both timestamps this is compared
    ///         against are this system's own, so they are at least measured the
    ///         same way.
    ///     </para>
    ///     <para>
    ///         Null on rows written before this column existed.
    ///     </para>
    /// </summary>
    public DateTimeOffset? SucceededAt { get; private set; }

    /// <summary>
    ///     Which path started the refund. Null until one does.
    /// </summary>
    public RefundCause? RefundCause { get; private set; }

    // Persisted as one nullable decimal column (the backing field, mapped in
    // TransactionConfiguration) but exposed as Money?, paired with the one
    // currency this transaction has.
    //
    // docs/adr/0015 originally left this a bare decimal, reasoning that a
    // second currency column could only ever agree with Amount's. That
    // storage argument still holds and there is no new column here. What did
    // not hold is the leap to leaving it untyped: CancelBookingHandler then
    // had to pair the currency back on by hand, in the one place where
    // getting it wrong costs real money.
    //
    // Set only once MarkRefundPending computes it - a cancellation policy's
    // tiered percentage, applied by the caller (CancelBookingHandler, via
    // ITransactionReversal), not necessarily equal to Amount. Transactions
    // has no notion of a cancellation policy itself - it just records
    // whatever amount it was told to refund.
    private decimal? _refundAmount;

    /// <summary>
    ///     The name of the backing field above, for the EF.Property lookups
    ///     TransactionReversal needs - a computed property is not translatable
    ///     to SQL, and a bare string there would drift silently if the field
    ///     were ever renamed.
    /// </summary>
    public const string RefundAmountField = nameof(_refundAmount);

    public Money? RefundAmount => _refundAmount is { } amount ? Money.Of(amount, Amount.Currency) : null;

    public static Transaction Create(Guid bookingId, Money amount)
    {
        Guard.Against.Default(bookingId);
        Guard.Against.NegativeOrZero(amount.Amount);

        return new Transaction(Guid.CreateVersion7(), bookingId, amount, TransactionStatus.Pending);
    }

    // Both transitions guard "only from Pending" - a transaction is a
    // one-shot ledger entry, not something that flips back and forth.
    // Unlike Cancel()'s idempotent no-op, re-finalizing is always a
    // genuine conflict worth surfacing - a retried webhook for an
    // already-succeeded transaction shouldn't be silently swallowed.
    public void MarkSucceeded(DateTimeOffset succeededAt)
    {
        if (TransactionStatus != TransactionStatus.Pending)
        {
            throw new TransactionAlreadyFinalizedException(Id);
        }

        TransactionStatus = TransactionStatus.Succeeded;
        SucceededAt = succeededAt;
    }

    public void MarkFailed(string? reason)
    {
        if (TransactionStatus != TransactionStatus.Pending)
        {
            throw new TransactionAlreadyFinalizedException(Id);
        }

        TransactionStatus = TransactionStatus.Failed;
        FailureReason = reason;
    }

    // The refund sub-lifecycle - only reachable from Succeeded, mirroring
    // MarkSucceeded/MarkFailed's "guard the starting state" shape. Driven
    // by CancelBookingHandler (via ITransactionReversal), and resolved by
    // the same admin stand-in endpoints MarkTransactionSucceeded/
    // MarkTransactionFailed use in place of a real gateway webhook.
    /// <summary>
    ///     Whether this path is the one that should write the refund, given when
    ///     the payment succeeded relative to when the booking was cancelled.
    ///     <para>
    ///         Two paths reach MarkRefundPending for one transaction and they
    ///         disagree about the amount: the cancellation path knows the policy
    ///         figure, the delayed payment-confirmation path only ever refunds in
    ///         full. Both were guarded on status alone, so whichever outbox
    ///         dispatch happened to run first decided the money - identical
    ///         business history producing 50% or 100% depending on scheduling,
    ///         with the loser silently swallowing its own decision.
    ///     </para>
    ///     <para>
    ///         The rule is ordering, not arrival. A payment that succeeded
    ///         <em>before</em> the cancellation bought a stay the guest then
    ///         cancelled, so the cancellation policy applies. One that succeeded
    ///         <em>after</em> bought nothing, so the whole amount goes back. That
    ///         is also what already happens whenever the two events are cleanly
    ///         separated - a transaction still Pending at cancel time is left
    ///         alone by the cancellation path, and the later confirmation refunds
    ///         in full - so this makes the overlapping window agree with the rest
    ///         rather than inventing a rule for it.
    ///     </para>
    ///     <para>
    ///         The two callers pass complementary causes, so exactly one of them
    ///         owns any given case and neither has to know the other's amount.
    ///         That matters: the payment path cannot compute a policy refund, and
    ///         does not have to, because whenever the policy figure is the right
    ///         answer the cancellation path is guaranteed to have seen this
    ///         transaction already Succeeded.
    ///     </para>
    ///     <para>
    ///         A null <paramref name="cancelledAt"/> means no cancellation is in
    ///         play - the payment failed to buy anything for some other reason,
    ///         such as a hold released underneath it - and the full amount is
    ///         owed. A null <see cref="SucceededAt"/> is a row from before that
    ///         column existed; it is treated as having succeeded first, which
    ///         hands the case to the cancellation path and preserves the
    ///         pre-existing behaviour for old rows.
    ///     </para>
    /// </summary>
    public bool RefundOwedIsThisPathsToWrite(RefundCause cause, DateTimeOffset? cancelledAt)
    {
        bool paidAfterTheCancellation = cancelledAt is null
                                        || (SucceededAt is { } succeeded && succeeded > cancelledAt.Value);

        return cause == Entities.RefundCause.PaymentUnusable
            ? paidAfterTheCancellation
            : !paidAfterTheCancellation;
    }

    public void MarkRefundPending(Money refundAmount, RefundCause cause)
    {
        // First writer wins, and a second attempt is a no-op rather than an
        // overwrite. The ordering rule above means the two paths should never
        // both decide they own a case, so reaching here twice is a bug
        // somewhere - but silently replacing a recorded refund amount is the one
        // outcome that must not be possible, because nothing downstream would
        // ever show that it happened.

        if (_refundAmount is not null)
        {
            return;
        }

        if (TransactionStatus != TransactionStatus.Succeeded)
        {
            throw new TransactionAlreadyFinalizedException(Id);
        }

        // This guard STAYS, and typing RefundAmount as Money? is exactly why
        // it has to. Only the decimal is stored; the currency on the way back
        // out is derived from Amount. So a mismatched refund would not be
        // rejected by the type - it would be silently relabelled as this
        // transaction's currency, which is worse than the reattachment the
        // typing removed. The guard is what licenses discarding the incoming
        // currency in the first place.
        if (refundAmount.Currency != Amount.Currency)
        {
            throw new CurrencyMismatchException(refundAmount.Currency, Amount.Currency);
        }

        Guard.Against.OutOfRange(refundAmount.Amount, nameof(refundAmount), 0m, Amount.Amount);

        TransactionStatus = TransactionStatus.RefundPending;
        _refundAmount = refundAmount.Amount;
        RefundCause = cause;
    }

    public void MarkRefunded()
    {
        if (TransactionStatus != TransactionStatus.RefundPending)
        {
            throw new TransactionAlreadyFinalizedException(Id);
        }

        TransactionStatus = TransactionStatus.Refunded;
    }

    public void MarkRefundFailed(string? reason)
    {
        if (TransactionStatus != TransactionStatus.RefundPending)
        {
            throw new TransactionAlreadyFinalizedException(Id);
        }

        TransactionStatus = TransactionStatus.RefundFailed;
        FailureReason = reason;
    }
}
