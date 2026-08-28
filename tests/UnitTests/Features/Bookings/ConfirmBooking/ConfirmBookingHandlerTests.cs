using Bookings;
using Bookings.Features.ConfirmBooking;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace UnitTests.Features.Bookings.ConfirmBooking;

// Proves the compensating-rollback fix: if the Bookings-side write fails
// after Catalog's hold has already been flipped to 'booked', the hold must
// be released back to 'held' rather than left permanently stuck (same
// idiom BecomeHostHandler already uses for its own cross-DbContext write).
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
            .Options;

        _dbContext = new AppBookingsDbContext(options);
        // Deliberately not calling Database.EnsureCreated() - the bookings
        // table doesn't exist, so SaveChangesAsync in the handler fails
        // deterministically, simulating the "Catalog write succeeded,
        // Bookings write failed" window.
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Handle_WhenBookingSaveFails_ReleasesTheHoldBackToHeld()
    {
        // Arrange
        Guid holdId = Guid.NewGuid();
        ConfirmedHold hold = new ConfirmedHold
        {
            UnitId = Guid.NewGuid(),
            CheckIn = DateOnly.FromDateTime(DateTime.UtcNow),
            CheckOut = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2),
            GuestCount = 2,
            TotalPrice = Money.Of(200m, Currency.KWD),
            Subtotal = 200m
        };

        Mock<IHoldConfirmation> holdConfirmationMock = new Mock<IHoldConfirmation>();
        holdConfirmationMock.Setup(x => x.ConfirmHoldAsync(holdId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hold);

        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns((Guid?)null);

        Mock<IPromotionRedemption> promotionRedemptionMock = new Mock<IPromotionRedemption>();

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

        ConfirmBookingHandler handler = new ConfirmBookingHandler(
            _dbContext, holdConfirmationMock.Object, promotionRedemptionMock.Object, unitLookupMock.Object,
            currentUserProviderMock.Object, TimeProvider.System);

        ConfirmBookingRequest request = new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        };

        // Act & Assert - the underlying Sqlite failure (missing table)
        // propagates, but the compensation must have run first.
        await Assert.ThrowsAnyAsync<DbUpdateException>(() =>
            handler.Handle(request, CancellationToken.None).AsTask());

        holdConfirmationMock.Verify(
            x => x.ReleaseHoldAsync(holdId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Handle_WhenBookingSaveFailsAndReleaseHoldAlsoFails_ThrowsAggregateExceptionContainingBoth()
    {
        // Arrange - the more interesting failure than the single-write
        // case above: compensation itself fails too. A bare `throw;` in
        // that inner catch would only ever surface whichever exception
        // happened to be thrown last, silently losing the other -
        // specifically, losing *why the booking save failed in the first
        // place*, the one piece of information most needed to diagnose a
        // hold that's now stuck 'booked' with nothing left to release it.
        Guid holdId = Guid.NewGuid();
        ConfirmedHold hold = new ConfirmedHold
        {
            UnitId = Guid.NewGuid(),
            CheckIn = DateOnly.FromDateTime(DateTime.UtcNow),
            CheckOut = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2),
            GuestCount = 2,
            TotalPrice = Money.Of(200m, Currency.KWD),
            Subtotal = 200m
        };

        Mock<IHoldConfirmation> holdConfirmationMock = new Mock<IHoldConfirmation>();
        holdConfirmationMock.Setup(x => x.ConfirmHoldAsync(holdId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hold);

        InvalidOperationException releaseFailure = new InvalidOperationException("Catalog is unreachable.");
        holdConfirmationMock.Setup(x => x.ReleaseHoldAsync(holdId, It.IsAny<CancellationToken>()))
            .ThrowsAsync(releaseFailure);

        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns((Guid?)null);

        Mock<IPromotionRedemption> promotionRedemptionMock = new Mock<IPromotionRedemption>();

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

        ConfirmBookingHandler handler = new ConfirmBookingHandler(
            _dbContext, holdConfirmationMock.Object, promotionRedemptionMock.Object, unitLookupMock.Object,
            currentUserProviderMock.Object, TimeProvider.System);

        ConfirmBookingRequest request = new ConfirmBookingRequest
        {
            HoldId = holdId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        };

        // Act & Assert - both failures come out together, neither dropped.
        AggregateException aggregate = await Assert.ThrowsAsync<AggregateException>(() =>
            handler.Handle(request, CancellationToken.None).AsTask());

        Assert.Equal(2, aggregate.InnerExceptions.Count);
        Assert.Contains(aggregate.InnerExceptions, ex => ex is DbUpdateException);
        Assert.Contains(releaseFailure, aggregate.InnerExceptions);
    }
}
