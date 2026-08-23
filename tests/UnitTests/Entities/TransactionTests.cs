using BuildingBlocks.Exceptions;
using SeedWork.Enums;
using Transactions.Entities;
namespace UnitTests.Entities;

public class TransactionTests
{
    private static Transaction CreateValidTransaction()
    {
        return Transaction.Create(Guid.NewGuid(), 100m, Currency.KWD);
    }

    [Fact]
    public void Create_ShouldSetAllProperties_WhenInputIsValid()
    {
        Guid bookingId = Guid.NewGuid();

        Transaction transaction = Transaction.Create(bookingId, 100m, Currency.KWD);

        Assert.NotEqual(Guid.Empty, transaction.Id);
        Assert.Equal(bookingId, transaction.BookingId);
        Assert.Equal(100m, transaction.Amount);
        Assert.Equal(Currency.KWD, transaction.Currency);
        Assert.Equal(TransactionStatus.Pending, transaction.TransactionStatus);
        Assert.Null(transaction.FailureReason);
    }

    [Fact]
    public void Create_ShouldThrow_WhenBookingIdIsEmpty()
    {
        Assert.ThrowsAny<ArgumentException>(() => Transaction.Create(Guid.Empty, 100m, Currency.KWD));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Create_ShouldThrow_WhenAmountIsNotPositive(decimal amount)
    {
        Assert.ThrowsAny<ArgumentException>(() => Transaction.Create(Guid.NewGuid(), amount, Currency.KWD));
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
}
