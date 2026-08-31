using Bookings.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Outbox;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Text.Json;
using Transactions;
using Transactions.Entities;
using Transactions.Outbox;
using Transactions.Serialization;
namespace UnitTests.Features.Transactions.Outbox;

// Proves the fix for a dead-lettered ConfirmBookingPaymentOutboxMessage
// leaving a Succeeded transaction with a permanently Pending booking behind
// it - money taken, nothing sold, with no automatic path back. See
// TransactionsOutboxDispatcher.OnDeadLetteredAsync and docs/adr/0003.
public class TransactionsOutboxDispatcherTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppTransactionsDbContext _dbContext;

    public TransactionsOutboxDispatcherTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppTransactionsDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        _dbContext = new AppTransactionsDbContext(options);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task TryDispatchAsync_WhenAConfirmBookingPaymentMessageDeadLetters_RefundsTheTransaction_AndResolvesTheMessage()
    {
        // Arrange - a transaction that succeeded, with a booking
        // confirmation that will never succeed (ConfirmPaymentAsync throwing
        // NotFoundException is the realistic, non-transient way this
        // actually happens - simulated here as any persistent failure).
        Transaction transaction = Transaction.Create(Guid.NewGuid(), Money.Of(100m, Currency.KWD));
        transaction.MarkSucceeded();
        _dbContext.Transactions.Add(transaction);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Mock<IBookingPaymentConfirmation> bookingPaymentConfirmationMock = new Mock<IBookingPaymentConfirmation>();
        bookingPaymentConfirmationMock
            .Setup(x => x.ConfirmPaymentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Booking not found."));

        TransactionsOutboxDispatcher dispatcher = new TransactionsOutboxDispatcher(
            _dbContext, bookingPaymentConfirmationMock.Object, TimeProvider.System,
            NullLogger<TransactionsOutboxDispatcher>.Instance);

        // One attempt short of OutboxDispatcherBase's own MaxAttempts (10) -
        // this dispatch is the one that pushes it over and triggers
        // OnDeadLetteredAsync.
        OutboxMessage message = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = nameof(ConfirmBookingPaymentOutboxMessage),
            Payload = JsonSerializer.Serialize(
                new ConfirmBookingPaymentOutboxMessage(transaction.Id, transaction.BookingId),
                TransactionsJsonSerializerContext.Default.ConfirmBookingPaymentOutboxMessage),
            CreatedAt = DateTimeOffset.UtcNow,
            NextAttemptAt = DateTimeOffset.UtcNow,
            Attempts = 9
        };
        _dbContext.TransactionsOutboxMessages.Add(message);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Act
        await dispatcher.TryDispatchAsync(message, TestContext.Current.CancellationToken);

        // Assert - refunded rather than left Succeeded forever.
        Transaction reloaded = await _dbContext.Transactions.AsNoTracking()
            .SingleAsync(t => t.Id == transaction.Id, TestContext.Current.CancellationToken);
        Assert.Equal(TransactionStatus.RefundPending, reloaded.TransactionStatus);
        Assert.Equal(100m, reloaded.RefundAmount);

        // The message itself must be resolved (not just dead-lettered) -
        // otherwise SweepDeadLetteredAsync would keep retrying
        // ConfirmPaymentAsync on it, which could confirm the booking after
        // the transaction was already marked for refund.
        Assert.Equal(10, message.Attempts);
        Assert.NotNull(message.ProcessedAt);
        Assert.Null(message.DeadLetteredAt);
    }
}
