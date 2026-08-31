using Availability.Contracts;
using Bookings;
using Bookings.Entities;
using Bookings.Features.CancelBooking;
using Bookings.Outbox;
using BuildingBlocks.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Persistence.Interceptors;
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

    [Fact]
    public async Task Handle_OnRecancelOfAnAlreadyRefundedBooking_ReportsTheRealRefund_NotNull()
    {
        // Arrange - a booking that was already cancelled and whose refund
        // already reached the refund sub-lifecycle (transaction moved past
        // Succeeded to RefundPending/Refunded) on some earlier call.
        // GetSucceededTransactionAmountAsync no longer sees it - that's the
        // whole point of the state having moved on - so it must not be
        // consulted before GetRefundSnapshotAsync, which still can.
        Booking booking = await SeedBookingAsync();
        booking.Cancel();
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Mock<ITransactionReversal> transactionReversalMock = new Mock<ITransactionReversal>();
        transactionReversalMock
            .Setup(x => x.GetSucceededTransactionAmountAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Money?)null);
        transactionReversalMock
            .Setup(x => x.GetRefundSnapshotAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new TransactionRefundSnapshot { Amount = 200m, RefundAmount = 100m });

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

        // Assert - the real recorded refund is reported, not null - a
        // booking that was genuinely refunded must never read back as
        // "nothing to refund" on an idempotent recancel.
        Assert.Equal(100m, response.RefundAmount);
        Assert.NotNull(response.Currency);
        Assert.Equal(50m, response.RefundPercent);
        Assert.False(response.RefundPending);
    }

    [Fact]
    public async Task Handle_OnRecancelBeforeTheOriginalReversalLands_ResolvesTheRefundAsOfTheOriginalCancellation_NotToday()
    {
        // Arrange - a dedicated context with the real audit interceptor
        // wired in (the shared fixture's _dbContext doesn't have one), so
        // booking.ModifiedAt actually reflects the SaveChangesAsync that
        // runs Cancel() below, same as the real app.
        using SqliteConnection connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        FakeTimeProvider timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns(_customerId);

        var options = new DbContextOptionsBuilder<AppBookingsDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .AddInterceptors(new AuditableEntitySaveChangesInterceptor(currentUserProviderMock.Object, timeProvider))
            .Options;

        await using AppBookingsDbContext dbContext = new AppBookingsDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        // Check-in 6 days out from "today" (T1) - the default policy's 5+
        // day tier (100%) applies at T1, but its 1-4 day tier (50%) would
        // apply if the same booking were resolved 2 days later (T2)
        // instead.
        DateOnly checkIn = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime).AddDays(6);
        Booking booking = Booking.Create(
            Guid.CreateVersion7(), Guid.NewGuid(), Guid.NewGuid(), _customerId,
            "Jane Guest", "jane@example.com", null,
            checkIn, checkIn.AddDays(2),
            2, Money.Of(200m, Currency.KWD), 200m, CancellationPolicy.CreateDefault());

        dbContext.Bookings.Add(booking);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Mock<ITransactionReversal> transactionReversalMock = new Mock<ITransactionReversal>();
        transactionReversalMock
            .Setup(x => x.GetSucceededTransactionAmountAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Money.Of(200m, Currency.KWD));
        transactionReversalMock
            .Setup(x => x.ReverseTransactionAsync(booking.Id, It.IsAny<Money>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Transactions is temporarily unreachable."));
        transactionReversalMock
            .Setup(x => x.GetRefundSnapshotAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync((TransactionRefundSnapshot?)null); // nothing ever lands, on either call

        BookingsOutboxDispatcher dispatcher = new BookingsOutboxDispatcher(
            dbContext, new Mock<IHoldConfirmation>().Object, transactionReversalMock.Object,
            new Mock<IPromotionRedemption>().Object, timeProvider, NullLogger<BookingsOutboxDispatcher>.Instance);

        CancelBookingHandler handler = new CancelBookingHandler(
            dbContext, dispatcher, transactionReversalMock.Object, currentUserProviderMock.Object, timeProvider);

        CancelBookingRequest request = new CancelBookingRequest { BookingId = booking.Id };

        // Act - cancel at T1 (100% tier); the inline dispatch fails
        // (arranged above), so nothing lands. Advance the clock past the
        // default policy's 5-day boundary, then recancel before the
        // (still-failing) reversal has landed.
        CancelBookingResponse firstResponse = await handler.Handle(request, TestContext.Current.CancellationToken);
        timeProvider.Advance(TimeSpan.FromDays(2));
        CancelBookingResponse secondResponse = await handler.Handle(request, TestContext.Current.CancellationToken);

        // Assert - both responses report the tier that applied when the
        // booking was actually cancelled (100%, T1), not the tier "today"
        // (T2, now inside the 50% window) would resolve to.
        Assert.Equal(100m, firstResponse.RefundPercent);
        Assert.Equal(200m, firstResponse.RefundAmount);
        Assert.True(firstResponse.RefundPending);

        Assert.Equal(100m, secondResponse.RefundPercent);
        Assert.Equal(200m, secondResponse.RefundAmount);
        Assert.True(secondResponse.RefundPending);
    }
}
