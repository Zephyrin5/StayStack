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
        Assert.Equal(60m, transaction.RefundAmount);
    }

    [Fact]
    public void MarkRefundPending_ShouldAllowAZeroRefund_WhenSucceeded()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        transaction.MarkRefundPending(Money.Of(0m, Currency.KWD));

        Assert.Equal(0m, transaction.RefundAmount);
    }

    [Fact]
    public void MarkRefundPending_ShouldAllowARefundEqualToTheFullAmount_WhenSucceeded()
    {
        Transaction transaction = CreateValidTransaction();
        transaction.MarkSucceeded();

        transaction.MarkRefundPending(transaction.Amount);

        Assert.Equal(transaction.Amount.Amount, transaction.RefundAmount);
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
}
