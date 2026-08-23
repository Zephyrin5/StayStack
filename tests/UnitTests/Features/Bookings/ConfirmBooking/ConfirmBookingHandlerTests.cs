using Bookings;
using Bookings.Features.ConfirmBooking;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using SeedWork.Enums;
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
            TotalPrice = 200m,
            Currency = Currency.KWD
        };

        Mock<IHoldConfirmation> holdConfirmationMock = new Mock<IHoldConfirmation>();
        holdConfirmationMock.Setup(x => x.ConfirmHoldAsync(holdId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(hold);

        Mock<ICurrentUserProvider> currentUserProviderMock = new Mock<ICurrentUserProvider>();
        currentUserProviderMock.Setup(x => x.UserId).Returns((Guid?)null);

        ConfirmBookingHandler handler = new ConfirmBookingHandler(
            _dbContext, holdConfirmationMock.Object, currentUserProviderMock.Object);

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
}
