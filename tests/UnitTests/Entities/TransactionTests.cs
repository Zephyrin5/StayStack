using SeedWork.Enums;
using SeedWork.ValueObjects;
using Transactions.Entities;
using Transactions.Exceptions;
namespace UnitTests.Entities;

public class TransactionTests
{
    private static Transaction CreateValidTransaction()
    {
        return Transaction.Create(Guid.NewGuid(), Money.Of(100m, Currency.KWD));
    }

    [Fact]
    public void Create_ShouldSetAllProperties_WhenInputIsValid()
    {
        Guid bookingId = Guid.NewGuid();

        Transaction transaction = Transaction.Create(bookingId, Money.Of(100m, Currency.KWD));

        Assert.NotEqual(Guid.Empty, transaction.Id);
        Assert.Equal(bookingId, transaction.BookingId);
        Assert.Equal(Money.Of(100m, Currency.KWD), transaction.Amount);
        Assert.Equal(TransactionStatus.Pending, transaction.TransactionStatus);
        Assert.Null(transaction.FailureReason);
    }

    [Fact]
    public void Create_ShouldThrow_WhenBookingIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => Transaction.Create(Guid.Empty, Money.Of(100m, Currency.KWD)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_ShouldThrow_WhenAmountIsNotPositive(decimal amount)
    {
        Assert.ThrowsAny<ArgumentException>(() => Transaction.Create(Guid.NewGuid(), Money.Of(amount, Currency.KWD)));
    }

    [Fact]
    public void MarkSucceeded_ShouldSetStatusToSucceeded()
    {
        Transaction transaction = CreateValidTransaction();

        transaction.MarkSucceeded();

        Assert.Equal(TransactionStatus.Succeeded, transaction.TransactionStatus);
    }

    [Fact]
    public void MarkSucceeded_ShouldThrow_WhenAlreadySucceeded()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        Assert.Throws<TransactionAlreadyFinalizedException>(transaction.MarkSucceeded);
    }

    [Fact]
    public void MarkSucceeded_ShouldThrow_WhenAlreadyFailed()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkFailed("Card declined");

        Assert.Throws<TransactionAlreadyFinalizedException>(transaction.MarkSucceeded);
    }

    [Fact]
    public void MarkFailed_ShouldSetStatusToFailedAndRecordReason()
    {
        Transaction transaction = CreateValidTransaction();

        transaction.MarkFailed("Card declined");

        Assert.Equal(TransactionStatus.Failed, transaction.TransactionStatus);
        Assert.Equal("Card declined", transaction.FailureReason);
    }

    [Fact]
    public void MarkFailed_ShouldThrow_WhenAlreadySucceeded()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        Assert.Throws<TransactionAlreadyFinalizedException>(() => transaction.MarkFailed("Card declined"));
    }

    [Fact]
    public void MarkRefundPending_ShouldSetStatusToRefundPendingAndRecordAmount_WhenSucceeded()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        transaction.MarkRefundPending(Money.Of(60m, Currency.KWD));

        Assert.Equal(TransactionStatus.RefundPending, transaction.TransactionStatus);
        Assert.Equal(Money.Of(60m, Currency.KWD), transaction.RefundAmount);
    }

    [Fact]
    public void MarkRefundPending_ShouldAllowAZeroRefund_WhenSucceeded()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        transaction.MarkRefundPending(Money.Of(0m, Currency.KWD));

        Assert.Equal(Money.Of(0m, Currency.KWD), transaction.RefundAmount);
    }

    [Fact]
    public void MarkRefundPending_ShouldAllowARefundEqualToTheFullAmount_WhenSucceeded()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        transaction.MarkRefundPending(transaction.Amount);

        Assert.Equal(transaction.Amount, transaction.RefundAmount);
    }

    [Fact]
    public void MarkRefundPending_ShouldThrow_WhenRefundAmountExceedsTheOriginalAmount()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        Assert.ThrowsAny<ArgumentException>(() => transaction.MarkRefundPending(transaction.Amount + Money.Of(1m, Currency.KWD)));
    }

    [Fact]
    public void MarkRefundPending_ShouldThrow_WhenRefundAmountIsNegative()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        Assert.ThrowsAny<ArgumentException>(() => transaction.MarkRefundPending(Money.Of(-1m, Currency.KWD)));
    }

    [Fact]
    public void MarkRefundPending_ShouldThrow_WhenStillPending()
    {
        Transaction transaction = CreateValidTransaction();

        Assert.Throws<TransactionAlreadyFinalizedException>(() => transaction.MarkRefundPending(Money.Of(50m, Currency.KWD)));
    }

    [Fact]
    public void MarkRefundPending_ShouldThrow_WhenAlreadyFailed()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkFailed("Card declined");

        Assert.Throws<TransactionAlreadyFinalizedException>(() => transaction.MarkRefundPending(Money.Of(50m, Currency.KWD)));
    }

    [Fact]
    public void MarkRefundPending_ShouldThrow_WhenCurrencyDoesNotMatch()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        // This guard is what licenses storing only the decimal. RefundAmount
        // derives its currency from Amount, so without this a USD refund
        // against a KWD transaction would come back out relabelled as KWD
        // rather than rejected - the type cannot catch what it reconstructs.
        Assert.Throws<CurrencyMismatchException>(() => transaction.MarkRefundPending(Money.Of(50m, Currency.USD)));
    }

    [Fact]
    public void MarkRefunded_ShouldSetStatusToRefunded_WhenRefundPending()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();
        transaction.MarkRefundPending(Money.Of(50m, Currency.KWD));

        transaction.MarkRefunded();

        Assert.Equal(TransactionStatus.Refunded, transaction.TransactionStatus);
    }

    [Fact]
    public void MarkRefunded_ShouldThrow_WhenStillSucceeded()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        Assert.Throws<TransactionAlreadyFinalizedException>(transaction.MarkRefunded);
    }

    [Fact]
    public void MarkRefundFailed_ShouldSetStatusToRefundFailedAndRecordReason_WhenRefundPending()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();
        transaction.MarkRefundPending(Money.Of(50m, Currency.KWD));

        transaction.MarkRefundFailed("Original card closed");

        Assert.Equal(TransactionStatus.RefundFailed, transaction.TransactionStatus);
        Assert.Equal("Original card closed", transaction.FailureReason);
    }

    [Fact]
    public void MarkRefundFailed_ShouldThrow_WhenStillSucceeded()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        Assert.Throws<TransactionAlreadyFinalizedException>(() => transaction.MarkRefundFailed("Original card closed"));
    }

    [Fact]
    public void RefundAmount_ShouldCarryTheTransactionsOwnCurrency_NotAnImpliedOne()
    {
        // The point of typing this Money? at all: callers get a currency with
        // the amount instead of pairing one on themselves.
        // CancelBookingHandler used to reach for booking.TotalPrice.Currency
        // to build its response - the same value, but asserted at the call
        // site, in the one place getting it wrong costs real money.
        Transaction transaction = Transaction.Create(Guid.NewGuid(), Money.Of(200m, Currency.KWD));
        transaction.MarkSucceeded();

        transaction.MarkRefundPending(Money.Of(60m, Currency.KWD));

        Assert.NotNull(transaction.RefundAmount);
        Assert.Equal(transaction.Amount.Currency, transaction.RefundAmount!.Value.Currency);
        Assert.Equal(60m, transaction.RefundAmount!.Value.Amount);
    }

    [Fact]
    public void RefundAmount_ShouldBeNull_BeforeAnyRefundIsRecorded()
    {
        // Null, not a zero-valued Money - "no refund recorded" and "a refund
        // of nothing" are different facts, and the nullable Money? keeps them
        // distinguishable the same way the nullable decimal did.
        Transaction transaction = Transaction.Create(Guid.NewGuid(), Money.Of(200m, Currency.KWD));

        Assert.Null(transaction.RefundAmount);
    }
}
