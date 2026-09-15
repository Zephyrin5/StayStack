using Ardalis.GuardClauses;
using SeedWork.Abstractions;
using SeedWork.ValueObjects;
using Transactions.Exceptions;
namespace Transactions.Entities;

public sealed class Transaction : Entity
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
    ///         Orders a payment against a cancellation, which decides which of
    ///         two refund amounts is owed (RefundDecision, docs/adr/0027).
    ///     </para>
    ///     <para>
    ///         Local observation time, not provider event time; nothing supplies
    ///         the latter. The cancellation instant it is compared with is also
    ///         this system's own clock.
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
    // currency this transaction has, so no caller pairs a currency by hand. A
    // second currency column could only ever agree with Amount's.
    //
    // Set by MarkRefundPending to the amount TransactionReversal decided
    // (RefundDecision), not necessarily Amount. Transactions has no notion of
    // a cancellation policy; it records the amount it is told.
    private decimal? _refundAmount;

    /// <summary>
    ///     The name of the backing field above, for the EF.Property lookups
    ///     TransactionReversal needs - a computed property is not translatable
    ///     to SQL, and a bare string there would drift silently if the field
    ///     were ever renamed.
    /// </summary>
    public const string RefundAmountField = nameof(_refundAmount);

    public Money? RefundAmount => _refundAmount is { } amount ? Money.Of(amount, Amount.Currency) : null;

    // The id is the caller's, never minted here. A factory that generates its
    // own identity hands a retried caller a different one on every attempt, and
    // a caller whose commit lost its acknowledgement can then never find the row
    // it already wrote (docs/adr/0025). ConfirmBookingHandler's booking id is
    // pre-generated for the same reason.
    public static Transaction Create(Guid id, Guid bookingId, Money amount)
    {
        Guard.Against.Default(id);
        Guard.Against.Default(bookingId);
        Guard.Against.NegativeOrZero(amount.Amount);

        return new Transaction(id, bookingId, amount, TransactionStatus.Pending);
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

    // The refund sub-lifecycle, only reachable from Succeeded. Resolved by the
    // admin stand-in endpoints MarkTransactionSucceeded/MarkTransactionFailed
    // use in place of a gateway webhook.
    public void MarkRefundPending(Money refundAmount, RefundCause cause)
    {
        // First writer wins, and a second attempt is a no-op rather than an
        // overwrite. The resolver decides once, so reaching here twice is a bug
        // somewhere - but silently replacing a recorded refund amount must not
        // be possible, because nothing downstream would show that it happened.

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
