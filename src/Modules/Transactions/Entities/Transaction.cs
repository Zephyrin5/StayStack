using Ardalis.GuardClauses;
using SeedWork.Abstractions;
using SeedWork.ValueObjects;
using Transactions.Exceptions;
namespace Transactions.Entities;

public sealed class Transaction : Entity
{
    // EF materialization only: its constructor binding cannot bind a complex property (docs/adr/0015).
    private Transaction()
    {
    }

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

    // Cross-module reference, so a plain Guid rather than an FK (docs/adr/0004).
    public Guid BookingId { get; private set; }

    // Snapshotted at initiation: what was charged does not drift with the booking's total.
    public Money Amount { get; private set; }

    // Not Status: Entity.Status is the soft-delete state.
    public TransactionStatus TransactionStatus { get; private set; }
    public string? FailureReason { get; private set; }

    /// <summary>
    ///     When this system observed the payment succeed, which orders it against a cancellation and so
    ///     decides the refund amount (docs/adr/0027). Local observation time; no provider event time exists.
    /// </summary>
    public DateTimeOffset? SucceededAt { get; private set; }

    /// <summary>Which path started the refund. Null until one does.</summary>
    public RefundCause? RefundCause { get; private set; }

    // One decimal column, exposed as Money? paired with Amount's currency, so no caller pairs one by
    // hand. Set by MarkRefundPending to the amount the resolver decided, not necessarily Amount.
    private decimal? _refundAmount;

    /// <summary>The backing field's name, for the EF.Property lookups TransactionReversal needs.</summary>
    public const string RefundAmountField = nameof(_refundAmount);

    public Money? RefundAmount => _refundAmount is { } amount ? Money.Of(amount, Amount.Currency) : null;

    public static Transaction Create(Guid id, Guid bookingId, Money amount)
    {
        Guard.Against.Default(id);
        Guard.Against.Default(bookingId);
        Guard.Against.NegativeOrZero(amount.Amount);

        return new Transaction(id, bookingId, amount, TransactionStatus.Pending);
    }

    // Both terminal transitions require Pending: a transaction is a one-shot ledger entry, and
    // re-finalizing is a conflict worth surfacing rather than swallowing.
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

    // The refund sub-lifecycle, reachable only from Succeeded.
    public void MarkRefundPending(Money refundAmount, RefundCause cause)
    {
        // First writer wins: silently replacing a recorded amount would leave no trace downstream.
        if (_refundAmount is not null)
        {
            return;
        }

        if (TransactionStatus != TransactionStatus.Succeeded)
        {
            throw new TransactionAlreadyFinalizedException(Id);
        }

        // Only the decimal is stored, so without this a mismatched currency would be silently
        // relabelled as this transaction's.
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
