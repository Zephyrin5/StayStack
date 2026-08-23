using Catalog;
using Catalog.Entities;
using Catalog.Enums;
using Catalog.Features.GetPriceCalendar;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.DependencyInjection;
using NpgsqlTypes;
using SeedWork.ValueObjects;
namespace IntegrationTests.Features.Catalog;

[Collection("Integration Tests")]
public class GetPriceCalendarHandlerTests(IntegrationTestWebApplicationFactory factory)
{
    private async Task SeedDatabaseAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private static Unit CreateTestUnit(decimal basePrice = 120m)
    {
        return Unit.Create(
            Guid.CreateVersion7(),
            UnitType.Room,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Ocean View Suite" } }, "en"),
            2,
            basePrice);
    }

    [Fact]
    public async Task Handle_NoHolds_ReturnsAllDaysAvailableWithUnitBasePrice()
    {
        // Arrange
        Unit unit = CreateTestUnit(150m);
        await SeedDatabaseAsync(unit);

        DateOnly from = new DateOnly(2026, 9, 1);
        DateOnly to = new DateOnly(2026, 9, 4); // 3 nights: Sept 1, Sept 2, Sept 3

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        HybridCache cache = scope.ServiceProvider.GetRequiredService<HybridCache>();
        GetPriceCalendarHandler handler = new GetPriceCalendarHandler(context, cache);

        GetPriceCalendarRequest request = new GetPriceCalendarRequest
        {
            UnitId = unit.Id,
            From = from,
            To = to
        };

        // Act
        GetPriceCalendarResponse response = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(response.Days);
        Assert.Equal(3, response.Days.Count);

        Assert.Collection(response.Days,
            day1 =>
            {
                Assert.Equal(new DateOnly(2026, 9, 1), day1.Date);
                Assert.Equal(150m, day1.Price);
                Assert.True(day1.IsAvailable);
            },
            day2 =>
            {
                Assert.Equal(new DateOnly(2026, 9, 2), day2.Date);
                Assert.Equal(150m, day2.Price);
                Assert.True(day2.IsAvailable);
            },
            day3 =>
            {
                Assert.Equal(new DateOnly(2026, 9, 3), day3.Date);
                Assert.Equal(150m, day3.Price);
                Assert.True(day3.IsAvailable);
            });
    }

    [Fact]
    public async Task Handle_ActiveHold_MarksHeldDaysUnavailableAndCheckOutDayAvailable()
    {
        // Verifies half-open interval [) boundary:
        // Hold from Sept 1 to Sept 3 means Sept 1 and Sept 2 are unavailable,
        // but Sept 3 (CheckOut) remains available for a new check-in.
        // Arrange
        Unit unit = CreateTestUnit();
        DateOnly from = new DateOnly(2026, 9, 1);
        DateOnly to = new DateOnly(2026, 9, 5);

        UnitAvailabilityHold hold = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = unit.Id,
            Status = "held",
            StayRange = new NpgsqlRange<DateOnly>(from, true, new DateOnly(2026, 9, 3), false) // [Sept 1, Sept 3)
        };

        await SeedDatabaseAsync(unit, hold);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        HybridCache cache = scope.ServiceProvider.GetRequiredService<HybridCache>();
        GetPriceCalendarHandler handler = new GetPriceCalendarHandler(context, cache);

        GetPriceCalendarRequest request = new GetPriceCalendarRequest { UnitId = unit.Id, From = from, To = to };

        // Act
        GetPriceCalendarResponse response = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.Equal(4, response.Days.Count);

        // Sept 1 & Sept 2 are held -> IsAvailable = false
        Assert.False(response.Days.First(d => d.Date == new DateOnly(2026, 9, 1)).IsAvailable);
        Assert.False(response.Days.First(d => d.Date == new DateOnly(2026, 9, 2)).IsAvailable);

        // Sept 3 (CheckOut) & Sept 4 are free -> IsAvailable = true
        Assert.True(response.Days.First(d => d.Date == new DateOnly(2026, 9, 3)).IsAvailable);
        Assert.True(response.Days.First(d => d.Date == new DateOnly(2026, 9, 4)).IsAvailable);
    }

    [Fact]
    public async Task Handle_ExpiredHeldRow_DoesNotBlockAvailability()
    {
        // An abandoned 'held' row past its hold_expires_at must show as
        // available even before anyone's HoldAvailabilityHandler cleanup
        // DELETE has actually removed the row - see the review finding
        // this closes (expired holds otherwise blocked inventory forever).
        // Arrange
        Unit unit = CreateTestUnit();
        DateOnly from = new DateOnly(2026, 9, 1);
        DateOnly to = new DateOnly(2026, 9, 3);

        UnitAvailabilityHold expiredHold = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = unit.Id,
            Status = "held",
            StayRange = new NpgsqlRange<DateOnly>(from, true, to, false),
            HoldExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };

        await SeedDatabaseAsync(unit, expiredHold);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        HybridCache cache = scope.ServiceProvider.GetRequiredService<HybridCache>();
        GetPriceCalendarHandler handler = new GetPriceCalendarHandler(context, cache);

        GetPriceCalendarRequest request = new GetPriceCalendarRequest { UnitId = unit.Id, From = from, To = to };

        // Act
        GetPriceCalendarResponse response = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.All(response.Days, day => Assert.True(day.IsAvailable));
    }

    [Fact]
    public async Task Handle_NonActiveHoldStatuses_DoesNotBlockAvailability()
    {
        // Statuses other than 'held' or 'booked' (e.g. 'released', 'expired') should be ignored by SQL
        // Arrange
        Unit unit = CreateTestUnit();
        DateOnly from = new DateOnly(2026, 9, 1);
        DateOnly to = new DateOnly(2026, 9, 3);

        UnitAvailabilityHold releasedHold = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = unit.Id,
            Status = "released",
            StayRange = new NpgsqlRange<DateOnly>(from, true, to, false)
        };

        await SeedDatabaseAsync(unit, releasedHold);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        HybridCache cache = scope.ServiceProvider.GetRequiredService<HybridCache>();
        GetPriceCalendarHandler handler = new GetPriceCalendarHandler(context, cache);

        GetPriceCalendarRequest request = new GetPriceCalendarRequest { UnitId = unit.Id, From = from, To = to };

        // Act
        GetPriceCalendarResponse response = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.All(response.Days, day => Assert.True(day.IsAvailable));
    }

    [Fact]
    public async Task Handle_UnitDoesNotExist_ReturnsEmptyDaysList()
    {
        // Arrange
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        HybridCache cache = scope.ServiceProvider.GetRequiredService<HybridCache>();
        GetPriceCalendarHandler handler = new GetPriceCalendarHandler(context, cache);

        GetPriceCalendarRequest request = new GetPriceCalendarRequest
        {
            UnitId = Guid.NewGuid(), // Unknown unit
            From = new DateOnly(2026, 9, 1),
            To = new DateOnly(2026, 9, 5)
        };

        // Act
        GetPriceCalendarResponse response = await handler.Handle(request, CancellationToken.None);

        // Assert
        Assert.NotNull(response);
        Assert.Empty(response.Days);
    }

    [Fact]
    public async Task Handle_SubsequentCallWithSameKey_ReturnsCachedResponse()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedDatabaseAsync(unit);

        DateOnly from = new DateOnly(2026, 9, 10);
        DateOnly to = new DateOnly(2026, 9, 12);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        HybridCache cache = scope.ServiceProvider.GetRequiredService<HybridCache>();
        GetPriceCalendarHandler handler = new GetPriceCalendarHandler(context, cache);

        GetPriceCalendarRequest request = new GetPriceCalendarRequest { UnitId = unit.Id, From = from, To = to };

        // 1. First invocation -> Hits DB & populates cache
        GetPriceCalendarResponse initialResponse = await handler.Handle(request, CancellationToken.None);
        Assert.All(initialResponse.Days, d => Assert.True(d.IsAvailable));

        // 2. Add a new hold directly to DB after initial call
        UnitAvailabilityHold newHold = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = unit.Id,
            Status = "booked",
            StayRange = new NpgsqlRange<DateOnly>(from, true, to, false)
        };
        await SeedDatabaseAsync(newHold);

        // 3. Second invocation -> Should serve stale cached response (all available) within TTL
        GetPriceCalendarResponse cachedResponse = await handler.Handle(request, CancellationToken.None);

        // Assert cached response is returned despite DB change
        Assert.All(cachedResponse.Days, d => Assert.True(d.IsAvailable));
    }
}
