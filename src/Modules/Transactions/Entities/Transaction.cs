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
    // same reasoning as Booking.GuestName being a snapshot: what was
    // actually charged shouldn't drift if the booking's own total ever
    // changed later. The one Money-typed (currency-carrying) field on this
    // entity - RefundAmount below is a plain decimal in this same currency
    // by construction (a transaction has exactly one currency, validated at
    // MarkRefundPending), matching how UnitAvailabilityHold.Subtotal stays
    // a plain decimal next to its own canonical TotalPrice (see
    // docs/adr/0015).
    public Money Amount { get; private set; }

    // Named TransactionStatus, not Status - Status is already claimed by
    // the inherited Entity.Status (EntityStatus: soft-delete state), same
    // reasoning as Booking.BookingStatus.
    public TransactionStatus TransactionStatus { get; private set; }
    public string? FailureReason { get; private set; }

    // Set only once MarkRefundPending computes it - a cancellation policy's
    // tiered percentage, applied against this transaction's own Amount by
    // the caller (Bookings.Features.CancelBooking, via
    // Transactions.Contracts.ITransactionReversal), not necessarily equal
    // to Amount itself. Transactions has no notion of a cancellation
    // policy - it just records whatever amount it was told to refund.
    public decimal? RefundAmount { get; private set; }

    public static Transaction Create(Guid bookingId, Money amount)
    {
        Guard.Against.Default(bookingId);
        Guard.Against.NegativeOrZero(amount.Amount);

        return new Transaction(Guid.CreateVersion7(), bookingId, amount, TransactionStatus.Pending);
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
    public void MarkRefundPending(Money refundAmount)
    {
        if (TransactionStatus != TransactionStatus.Succeeded)
        {
            throw new TransactionAlreadyFinalizedException(Id);
        }

        // A caller computing a refund in the wrong currency is exactly the
        // kind of bug Money's own currency-carrying arithmetic can't catch
        // by itself once the two values reach this boundary as independent
        // arguments - worth guarding explicitly rather than trusting it.
        if (refundAmount.Currency != Amount.Currency)
        {
            throw new CurrencyMismatchException(refundAmount.Currency, Amount.Currency);
        }

        Guard.Against.OutOfRange(refundAmount.Amount, nameof(refundAmount), 0m, Amount.Amount);

        TransactionStatus = TransactionStatus.RefundPending;
        RefundAmount = refundAmount.Amount;
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
