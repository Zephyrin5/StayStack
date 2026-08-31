using Availability.Contracts;
using Bookings;
using Bookings.Entities;
using Bookings.Features.CancelBooking;
using Bookings.Outbox;
using BuildingBlocks.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Promotions.Contracts;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using Transactions.Contracts;
namespace UnitTests.Features.Bookings.CancelBooking;

// Proves the fix for a response that couldn't distinguish "nothing to
// refund" from "a refund is queued but the inline dispatch attempt hasn't
// landed yet" - both used to read back as every refund field being null. See
// CancelBookingResponse.RefundPending's own doc comment and
// ITransactionReversal.GetSucceededTransactionAmountAsync.
public class CancelBookingHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppBookingsDbContext _dbContext;
    private readonly Guid _customerId = Guid.NewGuid();

    public CancelBookingHandlerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppBookingsDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        _dbContext = new AppBookingsDbContext(options);
        _dbContext.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task<Booking> SeedBookingAsync()
    {
        Booking booking = Booking.Create(
            Guid.CreateVersion7(), Guid.NewGuid(), Guid.NewGuid(), _customerId,
            "Jane Guest", "jane@example.com", null,
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2),
            2, Money.Of(200m, Currency.KWD), 200m, CancellationPolicy.CreateDefault());

        _dbContext.Bookings.Add(booking);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return booking;
    }

    [Fact]
    public async Task Handle_WhenTheInlineReverseTransactionDispatchFails_StillReportsTheComputedRefund_AsPending()
    {
        // Arrange - a booking with real money behind it (a Succeeded
        // transaction), where the inline dispatch attempt for
        // ReverseTransactionOutboxMessage fails transiently (simulated by
        // making ReverseTransactionAsync throw).
        Booking booking = await SeedBookingAsync();

        Mock<ITransactionReversal> transactionReversalMock = new Mock<ITransactionReversal>();
        transactionReversalMock
            .Setup(x => x.GetSucceededTransactionAmountAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Money.Of(200m, Currency.KWD));
        transactionReversalMock
            .Setup(x => x.ReverseTransactionAsync(booking.Id, It.IsAny<Money>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Transactions is temporarily unreachable."));
        transactionReversalMock
            .Setup(x => x.GetRefundSnapshotAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TransactionRefundSnapshot?)null); // nothing landed yet - the reversal never ran

        BookingsOutboxDispatcher dispatcher = new BookingsOutboxDispatcher(
            _dbContext, new Mock<IHoldConfirmation>().Object, transactionReversalMock.Object,
            new Mock<IPromotionRedemption>().Object, TimeProvider.System, NullLogger<BookingsOutboxDispatcher>.Instance);

        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns(_customerId);

        CancelBookingHandler handler = new CancelBookingHandler(
            _dbContext, dispatcher, transactionReversalMock.Object, currentUserProviderMock.Object, TimeProvider.System);

        CancelBookingRequest request = new CancelBookingRequest { BookingId = booking.Id };

        // Act
        CancelBookingResponse response = await handler.Handle(request, TestContext.Current.CancellationToken);

        // Assert - the computed figures are reported (the cancellation
        // policy resolves to a real percent for a booking cancelled this
        // far out, so this isn't the "genuinely nothing to refund" case),
        // marked pending rather than silently indistinguishable from "no
        // payment ever existed".
        Assert.NotNull(response.RefundAmount);
        Assert.NotNull(response.Currency);
        Assert.NotNull(response.RefundPercent);
        Assert.True(response.RefundPending);
    }

    [Fact]
    public async Task Handle_WhenThereWasNeverAPayment_ReportsNoRefund_NotPending()
    {
        // Arrange - no Succeeded transaction at all (never paid, or still
        // Pending) - genuinely nothing to refund, must not be reported as
        // pending.
        Booking booking = await SeedBookingAsync();

        Mock<ITransactionReversal> transactionReversalMock = new Mock<ITransactionReversal>();
        transactionReversalMock
            .Setup(x => x.GetSucceededTransactionAmountAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Money?)null);

        BookingsOutboxDispatcher dispatcher = new BookingsOutboxDispatcher(
            _dbContext, new Mock<IHoldConfirmation>().Object, transactionReversalMock.Object,
            new Mock<IPromotionRedemption>().Object, TimeProvider.System, NullLogger<BookingsOutboxDispatcher>.Instance);

        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns(_customerId);

        CancelBookingHandler handler = new CancelBookingHandler(
            _dbContext, dispatcher, transactionReversalMock.Object, currentUserProviderMock.Object, TimeProvider.System);

        CancelBookingRequest request = new CancelBookingRequest { BookingId = booking.Id };

        CancelBookingResponse response = await handler.Handle(request, TestContext.Current.CancellationToken);

        Assert.Null(response.RefundAmount);
        Assert.Null(response.Currency);
        Assert.Null(response.RefundPercent);
        Assert.False(response.RefundPending);
    }
}
