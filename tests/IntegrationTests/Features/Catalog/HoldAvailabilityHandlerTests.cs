using BuildingBlocks.Exceptions;
using Catalog;
using Catalog.Entities;
using Catalog.Features.HoldAvailability;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NpgsqlTypes;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace IntegrationTests.Features.Catalog;

[Collection("Integration Tests")]
public class HoldAvailabilityHandlerTests(IntegrationTestWebApplicationFactory factory)
{
    private async Task SeedDatabaseAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private static Unit CreateTestUnit(int maxCapacity = 2)
    {
        return Unit.Create(
            Guid.CreateVersion7(),
            UnitType.Room,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            maxCapacity,
            100);
    }

    [Fact]
    public async Task Handle_ValidRequest_CreatesHoldAndPersistsToDatabase()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedDatabaseAsync(unit);

        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = new HoldAvailabilityHandler(context, timeProvider);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(3),
            GuestCount = 2
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.HoldId);

        UnitAvailabilityHold? persistedHold = await context.UnitAvailabilityHolds
            .AsNoTracking()
            .FirstOrDefaultAsync(h => h.Id == result.HoldId);

        Assert.NotNull(persistedHold);
        Assert.Equal(unit.Id, persistedHold.UnitId);
        Assert.Equal(new NpgsqlRange<DateOnly>(today, true,
            today.AddDays(3), false), persistedHold.StayRange);
        Assert.Equal(2, persistedHold.GuestCount);
    }

    [Fact]
    public async Task Handle_AdjacentDateRanges_SucceedsWithoutExclusionViolation()
    {
        // Verifies half-open interval [) logic: CheckOut of Hold A == CheckIn of Hold B
        // Arrange
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        UnitAvailabilityHold existingHold = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = unit.Id,
            StayRange = new NpgsqlRange<DateOnly>(today, true, today.AddDays(2), false) // Aug 20 -> Aug 22
        };

        await SeedDatabaseAsync(unit, existingHold);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = new HoldAvailabilityHandler(context, timeProvider);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today.AddDays(2), // Aug 22 (Same day previous hold ends)
            CheckOut = today.AddDays(4), // Aug 24
            GuestCount = 2
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.HoldId);
    }

    [Fact]
    public async Task Handle_PostgresExclusionViolation_ThrowsUnitUnavailableException()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        UnitAvailabilityHold existingHold = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = unit.Id,
            StayRange = new NpgsqlRange<DateOnly>(today, today.AddDays(2))
        };

        await SeedDatabaseAsync(unit, existingHold);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = new HoldAvailabilityHandler(context, timeProvider);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(2),
            GuestCount = 2
        };

        // Act & Assert
        await Assert.ThrowsAsync<UnitUnavailableException>(() =>
            handler.Handle(command, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_UnitDoesNotExist_ThrowsNotFoundException()
    {
        // Arrange
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = new HoldAvailabilityHandler(context, timeProvider);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = Guid.NewGuid(), // Non-existent UnitId
            CheckIn = today,
            CheckOut = today.AddDays(2),
            GuestCount = 1
        };

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.Handle(command, CancellationToken.None).AsTask());
    }
}
