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
using Transactions.Contracts;
using Transactions.Entities;
using Transactions.Outbox;
using Transactions.Serialization;
namespace UnitTests.Features.Transactions.Outbox;

// A dead-lettered ConfirmBookingPaymentOutboxMessage must not leave a Succeeded
// transaction behind a permanently Pending booking - money taken, nothing sold,
// no automatic path back. See TransactionsOutboxDispatcher.OnDeadLetteredAsync
// and docs/adr/0003.
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
        Transaction transaction = Transaction.Create(Guid.CreateVersion7(), Guid.NewGuid(), Money.Of(100m, Currency.KWD));
        transaction.MarkSucceeded(DateTimeOffset.UtcNow);
        _dbContext.Transactions.Add(transaction);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Mock<IBookingPaymentConfirmation> bookingPaymentConfirmationMock = new Mock<IBookingPaymentConfirmation>();
        bookingPaymentConfirmationMock
            .Setup(x => x.ConfirmPaymentAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Booking not found."));

        // The lookup answers "no such booking", which is the case this test is
        // about: the confirmation exhausted its retries because the booking
        // genuinely was not there, so a refund is the right compensation.
        Mock<IBookingLookup> bookingLookupMock = new Mock<IBookingLookup>();
        bookingLookupMock
            .Setup(x => x.GetBookingDetailsAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((BookingAccessResult?)null);
        bookingLookupMock
            .Setup(x => x.GetRefundObligationAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((RefundObligationSnapshot?)null);

        // The real reversal, not a mock: the refund decision moved into it,
        // so mocking it would leave this test asserting nothing about the
        // behaviour it is named for. The booking lookup below answers "no such
        // booking", which is the case - no obligation, and none coming.
        TransactionReversal transactionReversal =
            new TransactionReversal(_dbContext, bookingLookupMock.Object, TimeProvider.System);

        TransactionsOutboxDispatcher dispatcher = new TransactionsOutboxDispatcher(
            _dbContext, bookingPaymentConfirmationMock.Object, bookingLookupMock.Object, transactionReversal,
            TimeProvider.System, NullLogger<TransactionsOutboxDispatcher>.Instance);

        // One attempt short of OutboxDispatcherBase's own MaxAttempts (10) -
        // this dispatch is the one that pushes it over and triggers
        // OnDeadLetteredAsync.
        OutboxMessage message = new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            Type = ConfirmBookingPaymentOutboxMessage.TypeName,
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
        Assert.Equal(Money.Of(100m, Currency.KWD), reloaded.RefundAmount);

        // The message itself must be resolved (not just dead-lettered) -
        // otherwise SweepDeadLetteredAsync would keep retrying
        // ConfirmPaymentAsync on it, which could confirm the booking after
        // the transaction was already marked for refund. Reloaded rather
        // than read off the original `message` reference: ClaimAndDispatchAsync
        // re-claims by id through its own query and clears the change tracker
        // first, so it does not mutate the instance the caller holds.
        OutboxMessage reloadedMessage = await _dbContext.TransactionsOutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Id == message.Id, TestContext.Current.CancellationToken);
        Assert.Equal(10, reloadedMessage.Attempts);
        Assert.NotNull(reloadedMessage.ProcessedAt);
        Assert.Null(reloadedMessage.DeadLetteredAt);
    }
}
