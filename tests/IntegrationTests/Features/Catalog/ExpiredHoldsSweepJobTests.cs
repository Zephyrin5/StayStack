using Catalog;
using Catalog.Entities;
using Catalog.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NpgsqlTypes;
namespace IntegrationTests.Features.Catalog;

[Collection("Integration Tests")]
public class ExpiredHoldsSweepJobTests(IntegrationTestWebApplicationFactory factory)
{
    private async Task SeedDatabaseAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    [Fact]
    public async Task SweepAsync_DeletesOnlyExpiredHeldRows_AcrossAllUnits()
    {
        // Arrange - HoldAvailabilityHandler's own cleanup only ever fires for
        // the one unit someone happens to retry; this sweep is the
        // catch-all for ranges nobody retries. Three rows, only one of
        // which should actually be deleted:
        DateTimeOffset now = new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

        UnitAvailabilityHold expiredHeld = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = Guid.NewGuid(),
            Status = "held",
            StayRange = new NpgsqlRange<DateOnly>(DateOnly.FromDateTime(now.UtcDateTime), true, DateOnly.FromDateTime(now.UtcDateTime).AddDays(2), false),
            HoldExpiresAt = now.AddMinutes(-1)
        };

        UnitAvailabilityHold stillLiveHeld = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = Guid.NewGuid(),
            Status = "held",
            StayRange = new NpgsqlRange<DateOnly>(DateOnly.FromDateTime(now.UtcDateTime), true, DateOnly.FromDateTime(now.UtcDateTime).AddDays(2), false),
            HoldExpiresAt = now.AddMinutes(5)
        };

        // A 'booked' row whose HoldExpiresAt happens to be in the past is
        // NOT stale - hold_expires_at stops being meaningful once a hold is
        // confirmed into a real booking, so the sweep must not touch it.
        UnitAvailabilityHold expiredButBooked = new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = Guid.NewGuid(),
            Status = "booked",
            StayRange = new NpgsqlRange<DateOnly>(DateOnly.FromDateTime(now.UtcDateTime), true, DateOnly.FromDateTime(now.UtcDateTime).AddDays(2), false),
            HoldExpiresAt = now.AddMinutes(-1)
        };

        await SeedDatabaseAsync(expiredHeld, stillLiveHeld, expiredButBooked);

        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);
        ExpiredHoldsSweepJob job = new ExpiredHoldsSweepJob(context, timeProvider);

        // Act
        await job.SweepAsync(null!, CancellationToken.None);

        // Assert
        List<Guid> remainingIds = await context.UnitAvailabilityHolds
            .AsNoTracking()
            .Select(h => h.Id)
            .ToListAsync();

        Assert.DoesNotContain(expiredHeld.Id, remainingIds);
        Assert.Contains(stillLiveHeld.Id, remainingIds);
        Assert.Contains(expiredButBooked.Id, remainingIds);
    }
}
