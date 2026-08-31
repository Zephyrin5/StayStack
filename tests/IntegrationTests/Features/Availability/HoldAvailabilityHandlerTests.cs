using Availability;
using Availability.Entities;
using Availability.Exceptions;
using Availability.Features.HoldAvailability;
using BuildingBlocks.Exceptions;
using Catalog;
using Catalog.Contracts;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NpgsqlTypes;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace IntegrationTests.Features.Availability;

[Collection("Integration Tests")]
public class HoldAvailabilityHandlerTests(IntegrationTestWebApplicationFactory factory)
{
    private async Task SeedCatalogAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private async Task SeedAvailabilityAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();

        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private static Unit CreateTestUnit(int maxCapacity = 2)
    {
        return Unit.Create(
            Guid.CreateVersion7(),
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            maxCapacity,
            100);
    }

    // Real IUnitLookup, not a mock - these tests verify actual pricing math
    // (PricingCalculator via Catalog's own database), which a mock would
    // defeat the purpose of. Resolved once per handler construction, same
    // as the real AppAvailabilityDbContext.
    private HoldAvailabilityHandler CreateHandler(AppAvailabilityDbContext context, TimeProvider timeProvider, IServiceScope scope)
    {
        IUnitLookup unitLookup = scope.ServiceProvider.GetRequiredService<IUnitLookup>();
        return new HoldAvailabilityHandler(context, unitLookup, timeProvider);
    }

    [Fact]
    public async Task Handle_ValidRequest_CreatesHoldAndPersistsToDatabase()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(3),
            GuestCount = 2,
            HolderToken = Guid.NewGuid().ToString()
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

        // Price/currency snapshotted at hold time (100/night * 3 nights),
        // not left to be recomputed from a possibly-changed unit price
        // later at confirm time.
        Assert.Equal(300m, persistedHold.TotalPrice.Amount);
        Assert.Equal(SeedWork.Enums.Currency.KWD, persistedHold.TotalPrice.Currency);

        // TimeProvider-derived, not DateTime.UtcNow - deterministic given
        // the FakeTimeProvider above.
        Assert.Equal(fixedInstant.AddMinutes(15).UtcDateTime, result.HoldExpiresAt);
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
            StayRange = new NpgsqlRange<DateOnly>(today, true, today.AddDays(2), false), // Aug 20 -> Aug 22
            TotalPrice = Money.Of(100m, Currency.KWD),
            Subtotal = 100m
        };

        await SeedCatalogAsync(unit);
        await SeedAvailabilityAsync(existingHold);

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today.AddDays(2), // Aug 22 (Same day previous hold ends)
            CheckOut = today.AddDays(4), // Aug 24
            GuestCount = 2,
            HolderToken = Guid.NewGuid().ToString()
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotNull(result);
        Assert.NotEqual(Guid.Empty, result.HoldId);
    }

    [Fact]
    public async Task Handle_ExpiredHeldRowOverlapsRequestedRange_DeletesStaleHoldAndSucceeds()
    {
        // A held row nobody ever confirmed or retried sits in 'held' past
        // its hold_expires_at otherwise forever - the exclusion constraint
        // has no WHERE clause of its own to ignore it, so the handler must
        // actively delete it before inserting. Proves that cleanup fires.
        // Arrange
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        UnitAvailabilityHold expiredHold = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = unit.Id,
            Status = "held",
            StayRange = new NpgsqlRange<DateOnly>(today, true, today.AddDays(2), false),
            HoldExpiresAt = fixedInstant.AddMinutes(-1),
            CreatedAt = fixedInstant.AddMinutes(-16),
            TotalPrice = Money.Of(100m, Currency.KWD),
            Subtotal = 100m
        };

        await SeedCatalogAsync(unit);
        await SeedAvailabilityAsync(expiredHold);

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(2),
            GuestCount = 2,
            HolderToken = Guid.NewGuid().ToString()
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert
        Assert.NotEqual(Guid.Empty, result.HoldId);

        List<UnitAvailabilityHold> holds = await context.UnitAvailabilityHolds
            .AsNoTracking()
            .Where(h => h.UnitId == unit.Id)
            .ToListAsync();

        // The stale row is gone, not just superseded - only the new hold remains.
        UnitAvailabilityHold onlyHold = Assert.Single(holds);
        Assert.Equal(result.HoldId, onlyHold.Id);
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
            StayRange = new NpgsqlRange<DateOnly>(today, today.AddDays(2)),
            TotalPrice = Money.Of(100m, Currency.KWD),
            Subtotal = 100m
        };

        await SeedCatalogAsync(unit);
        await SeedAvailabilityAsync(existingHold);

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(2),
            GuestCount = 2,
            HolderToken = Guid.NewGuid().ToString()
        };

        // Act & Assert
        await Assert.ThrowsAsync<UnitUnavailableException>(() =>
            handler.Handle(command, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_ActiveDateRangeOverride_ChargesOverridePriceForCoveredNights()
    {
        // Arrange - 3-night stay, the middle night is date-range overridden.
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        PricingRule overrideRule = PricingRule.CreateDateRangeOverride(
            unit.Id, today.AddDays(1), today.AddDays(2), 500m);

        await SeedCatalogAsync(unit, overrideRule);

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(3),
            GuestCount = 2,
            HolderToken = Guid.NewGuid().ToString()
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert - 100 (day 1) + 500 (overridden day 2) + 100 (day 3) = 700
        Assert.Equal(700m, result.TotalPrice);
    }

    [Fact]
    public async Task Handle_ActiveDayOfWeekMultiplier_ChargesMultipliedPriceForMatchingNights()
    {
        // Arrange - Aug 20 2026 is a Thursday; Aug 21 is a Friday.
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        PricingRule multiplierRule = PricingRule.CreateDayOfWeekMultiplier(unit.Id, [(int)DayOfWeek.Friday], 2m);

        await SeedCatalogAsync(unit, multiplierRule);

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(2), // Thu, Fri
            GuestCount = 2,
            HolderToken = Guid.NewGuid().ToString()
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert - 100 (Thu) + 200 (Fri, multiplied) = 300
        Assert.Equal(300m, result.TotalPrice);
    }

    [Fact]
    public async Task Handle_ActiveLengthOfStayDiscount_AppliesDiscountToSubtotal_WhenThresholdMet()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        PricingRule discountRule = PricingRule.CreateLengthOfStayDiscount(unit.Id, 7, 10m);

        await SeedCatalogAsync(unit, discountRule);

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(7),
            GuestCount = 2,
            HolderToken = Guid.NewGuid().ToString()
        };

        // Act
        HoldAvailabilityResponse result = await handler.Handle(command, CancellationToken.None);

        // Assert - 700 subtotal * 0.9 = 630
        Assert.Equal(630m, result.TotalPrice);
    }

    [Fact]
    public async Task Handle_UnitDoesNotExist_ThrowsNotFoundException()
    {
        // Arrange
        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = Guid.NewGuid(), // Non-existent UnitId
            CheckIn = today,
            CheckOut = today.AddDays(2),
            GuestCount = 1,
            HolderToken = Guid.NewGuid().ToString()
        };

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() =>
            handler.Handle(command, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_CheckInBeyondMaxLeadTime_ThrowsArgumentException()
    {
        // Without this, an anonymous caller could hold a unit for [today,
        // today+3650) and the exclusion constraint would faithfully enforce
        // that decade-long block - see HoldAvailabilityHandler's own
        // MaxLeadTimeDays constant.
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest command = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today.AddDays(731),
            CheckOut = today.AddDays(733),
            GuestCount = 1,
            HolderToken = Guid.NewGuid().ToString()
        };

        await Assert.ThrowsAsync<ArgumentException>(() =>
            handler.Handle(command, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_SixthActiveHoldForSameSession_ThrowsTooManyActiveHoldsException()
    {
        Unit unit = CreateTestUnit(maxCapacity: 10);
        await SeedCatalogAsync(unit);

        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);
        string holderToken = Guid.NewGuid().ToString();

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        // Five non-overlapping ranges on the same unit, same session token -
        // all should succeed, filling the cap exactly.
        for (int i = 0; i < 5; i++)
        {
            HoldAvailabilityRequest request = new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = today.AddDays(i * 3),
                CheckOut = today.AddDays(i * 3 + 2),
                GuestCount = 1,
                HolderToken = holderToken
            };
            await handler.Handle(request, CancellationToken.None);
        }

        // A 6th, on a range that doesn't even overlap the first five -
        // the cap is per-session, not per-unit/range, so a clean exclusion-
        // constraint check would otherwise let this through.
        HoldAvailabilityRequest sixthRequest = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today.AddDays(100),
            CheckOut = today.AddDays(102),
            GuestCount = 1,
            HolderToken = holderToken
        };

        await Assert.ThrowsAsync<TooManyActiveHoldsException>(() =>
            handler.Handle(sixthRequest, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task Handle_AfterFiveSuccessfulBookings_SixthHoldStillSucceeds()
    {
        // Regression test: the active-hold count previously included
        // 'booked' holds, which never revert to 'held' for a completed
        // booking (ConfirmHoldAsync sets 'booked' and nothing ever clears
        // it - ReconcileOrphanedBookedHoldsJob depends on that
        // persistence). That meant a real customer would be locked out of
        // new holds after their 5th completed booking. 'booked' rows must
        // never count toward the cap.
        Unit unit = CreateTestUnit(maxCapacity: 10);
        await SeedCatalogAsync(unit);

        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);
        string holderToken = Guid.NewGuid().ToString();

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();

        // Five already-'booked' holds under the same session token,
        // simulating five completed bookings - none of these are "active"
        // in any meaningful sense; the customer already has real bookings,
        // not open holds.
        for (int i = 0; i < 5; i++)
        {
            context.Add(new UnitAvailabilityHold
            {
                Id = Guid.NewGuid(),
                UnitId = unit.Id,
                Status = "booked",
                StayRange = new NpgsqlRange<DateOnly>(today.AddDays(i * 3), true, today.AddDays(i * 3 + 2), false),
                BookedAt = fixedInstant,
                HolderToken = holderToken,
                TotalPrice = Money.Of(200m, Currency.KWD),
                Subtotal = 200m
            });
        }

        await context.SaveChangesAsync();

        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest sixthRequest = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today.AddDays(100),
            CheckOut = today.AddDays(102),
            GuestCount = 1,
            HolderToken = holderToken
        };

        HoldAvailabilityResponse result = await handler.Handle(sixthRequest, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.HoldId);
    }

    [Fact]
    public async Task Handle_WithExpiredHeldRowsOnDifferentUnits_DoesNotCountThemTowardTheCap()
    {
        // Regression test: the inline per-unit cleanup DELETE only touches
        // the unit being held right now, so an expired 'held' row on a
        // different unit survives until ExpiredHoldsSweepJob reaps it (up
        // to 5 minutes later). The active-hold count must exclude expired
        // rows itself, otherwise a guest who abandons checkout on five
        // different units is locked out of a sixth with zero live holds.
        Unit unit = CreateTestUnit(maxCapacity: 10);
        await SeedCatalogAsync(unit);

        DateTimeOffset fixedInstant = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);
        DateOnly today = DateOnly.FromDateTime(fixedInstant.UtcDateTime);
        string holderToken = Guid.NewGuid().ToString();

        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();

        for (int i = 0; i < 5; i++)
        {
            context.Add(new UnitAvailabilityHold
            {
                Id = Guid.NewGuid(),
                UnitId = Guid.NewGuid(), // a different unit each time
                Status = "held",
                StayRange = new NpgsqlRange<DateOnly>(today, true, today.AddDays(2), false),
                HoldExpiresAt = fixedInstant.AddMinutes(-1), // already expired
                HolderToken = holderToken,
                TotalPrice = Money.Of(100m, Currency.KWD),
                Subtotal = 100m
            });
        }

        await context.SaveChangesAsync();

        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(fixedInstant);
        HoldAvailabilityHandler handler = CreateHandler(context, timeProvider, scope);

        HoldAvailabilityRequest sixthRequest = new HoldAvailabilityRequest
        {
            UnitId = unit.Id,
            CheckIn = today,
            CheckOut = today.AddDays(2),
            GuestCount = 1,
            HolderToken = holderToken
        };

        HoldAvailabilityResponse result = await handler.Handle(sixthRequest, CancellationToken.None);

        Assert.NotEqual(Guid.Empty, result.HoldId);
    }
}
