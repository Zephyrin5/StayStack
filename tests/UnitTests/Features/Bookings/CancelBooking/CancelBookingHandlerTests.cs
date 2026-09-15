using Microsoft.Extensions.Options;
using Bookings.Contracts;
using Bookings;
using Bookings.Entities;
using Bookings.Features.CancelBooking;
using BuildingBlocks.Identity;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Moq;
using Persistence.Interceptors;
using Promotions.Contracts;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using Transactions.Contracts;
using UnitTests.Persistence;
namespace UnitTests.Features.Bookings.CancelBooking;

// The cancellation response must distinguish "nothing to refund" from "a refund
// is owed and not yet recorded". See
// CancelBookingResponse.RefundPending's own doc comment and
// ITransactionReversal.GetPaymentStateAsync.
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
            // A week out. Cancellation is refused once the stay has started
            // (Booking.CanBeCancelledOn), and every test here is about what
            // the refund reporting does, not about the dates.
            DateOnly.FromDateTime(DateTime.UtcNow).AddDays(7), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(9),
            2, Money.Of(200m, Currency.KWD), Money.Of(200m, Currency.KWD), CancellationPolicy.CreateDefault(), "Asia/Kuwait", DateTimeOffset.UtcNow.AddMinutes(30));

        _dbContext.Bookings.Add(booking);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);
        return booking;
    }

    [Fact]
    public async Task Handle_WhenThePaymentIsStillAwaitingItsRefund_ReportsTheComputedRefund_AsPending()
    {
        // Arrange - a booking with real money behind it (a Succeeded
        // transaction) and no refund recorded against it yet.
        Booking booking = await SeedBookingAsync();

        Mock<ITransactionReversal> transactionReversalMock = new Mock<ITransactionReversal>();
        // One observation now, replacing the pair of reads that could straddle
        // a Succeeded -> RefundPending transition. Paid, nothing refunded yet.
        transactionReversalMock
            .Setup(x => x.GetPaymentStateAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStateSnapshot { Amount = Money.Of(200m, Currency.KWD), AwaitingRefund = true });


        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns(_customerId);

        CancelBookingHandler handler = new CancelBookingHandler(
            _dbContext, new SingleContextAtomicScope(_dbContext), new Mock<IHoldConfirmation>().Object,
            new Mock<IPromotionRedemption>().Object, transactionReversalMock.Object, currentUserProviderMock.Object,
            new Mock<IBookingSessions>().Object, TimeProvider.System);

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


        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns(_customerId);

        CancelBookingHandler handler = new CancelBookingHandler(
            _dbContext, new SingleContextAtomicScope(_dbContext), new Mock<IHoldConfirmation>().Object,
            new Mock<IPromotionRedemption>().Object, transactionReversalMock.Object, currentUserProviderMock.Object,
            new Mock<IBookingSessions>().Object, TimeProvider.System);

        CancelBookingRequest request = new CancelBookingRequest { BookingId = booking.Id };

        CancelBookingResponse response = await handler.Handle(request, TestContext.Current.CancellationToken);

        Assert.Null(response.RefundAmount);
        Assert.Null(response.Currency);
        Assert.Null(response.RefundPercent);
        Assert.False(response.RefundPending);
    }

    // Both settled outcomes, and they must be told apart: a refund that reached
    // the card and one the provider refused cannot both read as "not pending".
    [Theory]
    [InlineData(RefundStatus.Refunded)]
    [InlineData(RefundStatus.Failed)]
    public async Task Handle_OnRecancelOfAnAlreadyRefundedBooking_ReportsTheRealRefund_NotNull(RefundStatus settled)
    {
        // Arrange - a booking that was already cancelled and whose refund
        // already reached the refund sub-lifecycle (transaction moved past
        // Succeeded to RefundPending/Refunded) on some earlier call.
        // GetPaymentStateAsync sees the refund rather than "nothing succeeded",
        // whole point of the state having moved on - so it must not be
        // which is exactly what one coherent read buys.
        Booking booking = await SeedBookingAsync();
        booking.Cancel(DateTimeOffset.UtcNow);
        await _dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Mock<ITransactionReversal> transactionReversalMock = new Mock<ITransactionReversal>();
        // Settled - the transaction has moved past Succeeded, so the single read
        // reports the refund rather than "nothing to refund".
        transactionReversalMock
            .Setup(x => x.GetPaymentStateAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStateSnapshot
            {
                Amount = Money.Of(200m, Currency.KWD),
                RefundAmount = Money.Of(100m, Currency.KWD),
                RefundStatus = settled
            });


        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns(_customerId);

        CancelBookingHandler handler = new CancelBookingHandler(
            _dbContext, new SingleContextAtomicScope(_dbContext), new Mock<IHoldConfirmation>().Object,
            new Mock<IPromotionRedemption>().Object, transactionReversalMock.Object, currentUserProviderMock.Object,
            new Mock<IBookingSessions>().Object, TimeProvider.System);

        CancelBookingRequest request = new CancelBookingRequest { BookingId = booking.Id };

        // Act
        CancelBookingResponse response = await handler.Handle(request, TestContext.Current.CancellationToken);

        // Assert - the real recorded refund is reported, not null - a
        // booking that was genuinely refunded must never read back as
        // "nothing to refund" on an idempotent recancel.
        Assert.Equal(100m, response.RefundAmount);
        Assert.NotNull(response.Currency);
        Assert.Equal(50m, response.RefundPercent);
        Assert.Equal(settled, response.RefundStatus);
        Assert.False(response.RefundPending);
    }

    [Fact]
    public async Task Handle_OnRecancelWhileTheRefundIsStillOwed_ReportsItAsOfTheOriginalCancellation_NotToday()
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
            2, Money.Of(200m, Currency.KWD), Money.Of(200m, Currency.KWD), CancellationPolicy.CreateDefault(), "Asia/Kuwait", DateTimeOffset.UtcNow.AddMinutes(30));

        dbContext.Bookings.Add(booking);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Mock<ITransactionReversal> transactionReversalMock = new Mock<ITransactionReversal>();
        transactionReversalMock
            .Setup(x => x.GetPaymentStateAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStateSnapshot { Amount = Money.Of(200m, Currency.KWD), AwaitingRefund = true });


        CancelBookingHandler handler = new CancelBookingHandler(
            dbContext, new SingleContextAtomicScope(dbContext), new Mock<IHoldConfirmation>().Object,
            new Mock<IPromotionRedemption>().Object, transactionReversalMock.Object, currentUserProviderMock.Object,
            new Mock<IBookingSessions>().Object, timeProvider);

        CancelBookingRequest request = new CancelBookingRequest { BookingId = booking.Id };

        // Act - cancel at T1 (100% tier) with the refund still owed (arranged
        // above). Advance the clock past the default policy's 5-day boundary,
        // then recancel.
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

    [Fact]
    public async Task Handle_RefundTier_IsMeasuredAtTheProperty_NotInUtc()
    {
        // The money proof for docs/adr/0018.
        //
        // At 21:30 UTC on 2026-08-20 it is already 00:30 on the 21st in
        // Asia/Kuwait. Against a 2026-08-25 check-in that is 4 days out
        // locally - the default policy's 1-4 day tier, 50%. A UTC-derived
        // "today" reads the 20th, makes it 5 days, and pays the 100% tier
        // instead. Whichever side of UTC a property sits on, someone is paid
        // the wrong amount; here the business overpays.
        using SqliteConnection connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        FakeTimeProvider timeProvider = new FakeTimeProvider(
            new DateTimeOffset(2026, 8, 20, 21, 30, 0, TimeSpan.Zero));

        var options = new DbContextOptionsBuilder<AppBookingsDbContext>()
            .UseSqlite(connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        await using AppBookingsDbContext dbContext = new AppBookingsDbContext(options);
        await dbContext.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);

        Booking booking = Booking.Create(
            Guid.CreateVersion7(), Guid.NewGuid(), Guid.NewGuid(), _customerId,
            "Jane Guest", "jane@example.com", null,
            new DateOnly(2026, 8, 25), new DateOnly(2026, 8, 27),
            2, Money.Of(200m, Currency.KWD), Money.Of(200m, Currency.KWD), CancellationPolicy.CreateDefault(), "Asia/Kuwait", DateTimeOffset.UtcNow.AddMinutes(30));
        dbContext.Bookings.Add(booking);
        await dbContext.SaveChangesAsync(TestContext.Current.CancellationToken);

        Mock<ITransactionReversal> transactionReversalMock = new Mock<ITransactionReversal>();
        transactionReversalMock
            .Setup(x => x.GetPaymentStateAsync(booking.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PaymentStateSnapshot { Amount = Money.Of(200m, Currency.KWD), AwaitingRefund = true });

        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns(_customerId);

        CancelBookingHandler handler = new CancelBookingHandler(
            dbContext, new SingleContextAtomicScope(dbContext), new Mock<IHoldConfirmation>().Object,
            new Mock<IPromotionRedemption>().Object, transactionReversalMock.Object, currentUserProviderMock.Object,
            new Mock<IBookingSessions>().Object, timeProvider);

        CancelBookingResponse response = await handler.Handle(
            new CancelBookingRequest { BookingId = booking.Id }, TestContext.Current.CancellationToken);

        Assert.Equal(50m, response.RefundPercent);
        Assert.Equal(100m, response.RefundAmount);
    }
}
