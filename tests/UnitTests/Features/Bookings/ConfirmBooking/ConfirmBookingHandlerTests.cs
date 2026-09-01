using Availability.Contracts;
using Bookings;
using Bookings.Features.ConfirmBooking;
using Bookings.Outbox;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Outbox;
using Promotions.Contracts;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using Transactions.Contracts;
namespace UnitTests.Features.Bookings.ConfirmBooking;

// Proves the compensating-rollback fix: if the Bookings-side write fails
// after Catalog's hold has already been flipped to 'booked', the hold's
// release must be durably queued (via the outbox - see docs/adr/0003)
// rather than left permanently stuck, and must not be blocked by whether the
// release itself succeeds inline.
public class ConfirmBookingHandlerTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppBookingsDbContext _dbContext;

    public ConfirmBookingHandlerTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<AppBookingsDbContext>()
            .UseSqlite(_connection)
            .UseSnakeCaseNamingConvention()
            .Options;

        _dbContext = new AppBookingsDbContext(options);

        _dbContext.Database.EnsureCreated();

        // Then drop exactly one table, so the guest-checkout management-token
        // insert fails and rolls the whole save back with it - a
        // deterministic "the hold was already flipped, the Bookings-side
        // write then failed" without breaking anything else.
        //
        // This used to work by creating no schema at all beyond the outbox
        // table. That stopped being viable once ConfirmBookingHandler began
        // asking the database whether the Booking actually committed before
        // compensating (docs/adr/0017): with no bookings table, that read
        // throws instead of answering, and no compensation happens. Dropping
        // one table keeps the failure realistic - in production the schema
        // exists and the read succeeds - while still failing the save.
        _dbContext.Database.ExecuteSqlRaw("DROP TABLE booking_management_tokens");
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ConfirmedHold CreateHold() => new ConfirmedHold
    {
        UnitId = Guid.NewGuid(),
        CheckIn = DateOnly.FromDateTime(DateTime.UtcNow),
        CheckOut = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2),
        GuestCount = 2,
        TotalPrice = Money.Of(200m, Currency.KWD),
        Subtotal = 200m
    };

    private static Mock<IUnitLookup> CreateUnitLookupMock(ConfirmedHold hold)
    {
        Mock<IUnitLookup> unitLookupMock = new Mock<IUnitLookup>();
        unitLookupMock.Setup(x => x.GetUnitAsync(hold.UnitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnitSummary
            {
                Id = hold.UnitId,
                MaxOccupancy = hold.GuestCount,
                BasePrice = hold.TotalPrice,
                PropertyId = Guid.NewGuid(),
                HostId = Guid.NewGuid(),
                CancellationPolicy = CancellationPolicy.CreateDefault()
            });
        return unitLookupMock;
    }

    [Fact]
    public async Task Handle_WhenBookingSaveFails_ReleasesTheHoldBackToHeld()
    {
        // Arrange
        Guid holdId = Guid.NewGuid();
        ConfirmedHold hold = CreateHold();

        Mock<IHoldConfirmation> holdConfirmationMock = new Mock<IHoldConfirmation>();
        holdConfirmationMock.Setup(x => x.ConfirmHoldAsync(holdId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hold);

        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns((Guid?)null);

        Mock<IPromotionRedemption> promotionRedemptionMock = new Mock<IPromotionRedemption>();

        BookingsOutboxDispatcher dispatcher = new BookingsOutboxDispatcher(
            _dbContext, holdConfirmationMock.Object, new Mock<ITransactionReversal>().Object,
            promotionRedemptionMock.Object, TimeProvider.System, NullLogger<BookingsOutboxDispatcher>.Instance);

        ConfirmBookingHandler handler = new ConfirmBookingHandler(
            _dbContext, dispatcher, holdConfirmationMock.Object, promotionRedemptionMock.Object,
            CreateUnitLookupMock(hold).Object, currentUserProviderMock.Object, TimeProvider.System);

        ConfirmBookingRequest request = new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        };

        // Act & Assert - the underlying Sqlite failure (missing
        // booking_management_tokens table) propagates, but the compensating
        // enqueue+dispatch must have run first.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            handler.Handle(request, CancellationToken.None).AsTask());

        holdConfirmationMock.Verify(
            x => x.ReleaseHoldAsync(holdId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenBookingSaveFailsAndReleaseHoldAlsoFails_StillThrowsTheOriginalFailureAndQueuesTheReleaseForRetry()
    {
        // Arrange - the more interesting failure than the single-write case
        // above: the compensating release itself fails too (transiently).
        // The original booking-save failure still propagates untouched,
        // and the release is left durably queued (unprocessed, Attempts
        // incremented) for OutboxRelayJob to retry - not silently lost,
        // just no longer the handler's problem to report. See docs/adr/0003.
        Guid holdId = Guid.NewGuid();
        ConfirmedHold hold = CreateHold();

        Mock<IHoldConfirmation> holdConfirmationMock = new Mock<IHoldConfirmation>();
        holdConfirmationMock.Setup(x => x.ConfirmHoldAsync(holdId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hold);

        InvalidOperationException releaseFailure = new InvalidOperationException("Catalog is unreachable.");
        holdConfirmationMock.Setup(x => x.ReleaseHoldAsync(holdId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(releaseFailure);

        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns((Guid?)null);

        Mock<IPromotionRedemption> promotionRedemptionMock = new Mock<IPromotionRedemption>();

        BookingsOutboxDispatcher dispatcher = new BookingsOutboxDispatcher(
            _dbContext, holdConfirmationMock.Object, new Mock<ITransactionReversal>().Object,
            promotionRedemptionMock.Object, TimeProvider.System, NullLogger<BookingsOutboxDispatcher>.Instance);

        ConfirmBookingHandler handler = new ConfirmBookingHandler(
            _dbContext, dispatcher, holdConfirmationMock.Object, promotionRedemptionMock.Object,
            CreateUnitLookupMock(hold).Object, currentUserProviderMock.Object, TimeProvider.System);

        ConfirmBookingRequest request = new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        };

        // Act & Assert - the original failure comes through untouched.
        DbUpdateException bookingSaveFailure = await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            handler.Handle(request, CancellationToken.None).AsTask());
        Assert.NotNull(bookingSaveFailure);

        // The release attempt was made and failed, but is still there,
        // durably queued for OutboxRelayJob to pick up.
        OutboxMessage queuedMessage = await _dbContext.Set<OutboxMessage>().SingleAsync();
        Assert.Equal(nameof(ReleaseHoldOutboxMessage), queuedMessage.Type);
        Assert.Null(queuedMessage.ProcessedAt);
        Assert.Equal(1, queuedMessage.Attempts);
        Assert.Equal(releaseFailure.Message, queuedMessage.LastError);
    }
}

// Separate class, own schema-backed DbContext (unlike ConfirmBookingHandlerTests
// above, which deliberately runs against no schema at all) - these need the
// booking save to actually succeed so the confirmed price can be inspected.
public class ConfirmBookingHandlerPromoPricingTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppBookingsDbContext _dbContext;

    public ConfirmBookingHandlerPromoPricingTests()
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

    [Fact]
    public async Task Handle_WhenRedeemedDiscountIsSmallerThanTheLengthOfStayDiscountItReplaces_RejectsTheCodeButKeepsTheHold()
    {
        // Reproduces the reported bug: a hold quoted at 180 KWD (200
        // subtotal, 20 KWD LOS discount already applied), with a 5 KWD
        // promo redeemed on top. The promo applies against the pre-LOS
        // subtotal - naive arithmetic gives 200 - 5 = 195, MORE than the
        // 180 KWD the guest already saw. Rather than silently falling back
        // to 180 (burning the code for zero benefit), the code is rejected
        // outright: a promoCode validation error, and the redemption
        // already created is reversed. Unlike a genuinely invalid code,
        // the hold must NOT be released - the code was real and valid, so
        // the guest shouldn't lose their 15-minute hold just for trying
        // one that didn't beat their LOS discount.
        Guid holdId = Guid.NewGuid();
        ConfirmedHold hold = new ConfirmedHold
        {
            UnitId = Guid.NewGuid(),
            CheckIn = DateOnly.FromDateTime(DateTime.UtcNow),
            CheckOut = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2),
            GuestCount = 2,
            TotalPrice = Money.Of(180m, Currency.KWD),
            Subtotal = 200m,
            LengthOfStayDiscountAmount = 20m
        };

        Mock<IHoldConfirmation> holdConfirmationMock = new Mock<IHoldConfirmation>();
        holdConfirmationMock.Setup(x => x.ConfirmHoldAsync(holdId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hold);

        Mock<IPromotionRedemption> promotionRedemptionMock = new Mock<IPromotionRedemption>();
        promotionRedemptionMock
            .Setup(x => x.RedeemAsync(
                "SAVE5", hold.UnitId, "jane@example.com", Money.Of(hold.Subtotal, Currency.KWD),
                It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PromotionRedemptionResult { RedemptionId = Guid.NewGuid(), DiscountAmount = Money.Of(5m, Currency.KWD) });

        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns((Guid?)null);

        Mock<IUnitLookup> unitLookupMock = new Mock<IUnitLookup>();
        unitLookupMock.Setup(x => x.GetUnitAsync(hold.UnitId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UnitSummary
            {
                Id = hold.UnitId,
                MaxOccupancy = hold.GuestCount,
                BasePrice = hold.TotalPrice,
                PropertyId = Guid.NewGuid(),
                HostId = Guid.NewGuid(),
                CancellationPolicy = CancellationPolicy.CreateDefault()
            });

        BookingsOutboxDispatcher dispatcher = new BookingsOutboxDispatcher(
            _dbContext, holdConfirmationMock.Object, new Mock<ITransactionReversal>().Object,
            promotionRedemptionMock.Object, TimeProvider.System, NullLogger<BookingsOutboxDispatcher>.Instance);

        ConfirmBookingHandler handler = new ConfirmBookingHandler(
            _dbContext, dispatcher, holdConfirmationMock.Object, promotionRedemptionMock.Object,
            unitLookupMock.Object, currentUserProviderMock.Object, TimeProvider.System);

        ConfirmBookingRequest request = new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com",
            PromoCode = "SAVE5"
        };

        ValidationException validationException = await Assert.ThrowsAsync<ValidationException>(() =>
            handler.Handle(request, CancellationToken.None).AsTask());
        Assert.Contains("promoCode", validationException.Errors.Keys);

        holdConfirmationMock.Verify(x => x.ReleaseHoldAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
        promotionRedemptionMock.Verify(x => x.ReverseRedemptionAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Once);
    }
}
