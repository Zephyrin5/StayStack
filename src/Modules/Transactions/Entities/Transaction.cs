using Ardalis.GuardClauses;
using SeedWork.Abstractions;
using SeedWork.Enums;
using SeedWork.Interfaces;
using Transactions.Exceptions;
namespace Transactions.Entities;

public sealed class Transaction : Entity, IAggregateRoot
{
    // See Property.cs (Catalog) for why materialization goes through a
    // real constructor rather than a parameterless one + `required`/`null!`.
    private Transaction(
        Guid id,
        Guid bookingId,
        decimal amount,
        Currency currency,
        TransactionStatus transactionStatus)
    {
        Id = id;
        BookingId = bookingId;
        Amount = amount;
        Currency = currency;
        TransactionStatus = transactionStatus;
    }

    // Cross-module reference, plain Guid rather than a real FK - same
    // pattern as Booking.UnitId (Bookings referencing Catalog). Resolved
    // through Bookings.Contracts, never through a direct reference to
    // Bookings' own entities.
    public Guid BookingId { get; private set; }

    // Snapshotted from the booking at initiation time, not a live read -
    // same reasoning as Booking.GuestName being a snapshot: what was
    // actually charged shouldn't drift if the booking's own total ever
    // changed later.
    public decimal Amount { get; private set; }
    public Currency Currency { get; private set; }

    // Named TransactionStatus, not Status - Status is already claimed by
    // the inherited Entity.Status (EntityStatus: soft-delete state), same
    // reasoning as Booking.BookingStatus.
    public TransactionStatus TransactionStatus { get; private set; }
    public string? FailureReason { get; private set; }

    public static Transaction Create(Guid bookingId, decimal amount, Currency currency)
    {
        Guard.Against.Default(bookingId);
        Guard.Against.NegativeOrZero(amount);

        return new Transaction(Guid.CreateVersion7(), bookingId, amount, currency, TransactionStatus.Pending);
    }

    // Both transitions guard "only from Pending", same reasoning as
    // Booking.Cancel()/Confirm() - a transaction is a one-shot ledger
    // entry, not something that flips back and forth. Unlike Cancel()'s
    // idempotent no-op, re-finalizing a transaction is always a genuine
    // conflict worth surfacing (a retried webhook call for an
    // already-succeeded transaction should not be silently swallowed the
    // way a repeated cancel is).
    public void MarkSucceeded()
    {
        if (TransactionStatus != TransactionStatus.Pending)
        {
            throw new TransactionAlreadyFinalizedException(Id);
        }

        TransactionStatus = TransactionStatus.Succeeded;
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
    // MarkSucceeded/MarkFailed's own "one-shot, guard the starting state"
    // shape. Driven by CancelBookingHandler (via Transactions.Contracts.
    // ITransactionReversal) when a booking with a paid transaction is
    // cancelled, and resolved by the same kind of admin stand-in endpoint
    // MarkTransactionSucceeded/MarkTransactionFailed already use in place
    // of a real gateway webhook.
    public void MarkRefundPending()
    {
        if (TransactionStatus != TransactionStatus.Succeeded)
        {
            throw new TransactionAlreadyFinalizedException(Id);
        }

        TransactionStatus = TransactionStatus.RefundPending;
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
