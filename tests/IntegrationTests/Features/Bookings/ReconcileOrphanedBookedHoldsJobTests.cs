using Bookings;
using Bookings.Entities;
using Bookings.Jobs;
using Catalog;
using Catalog.Contracts;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using NpgsqlTypes;
using SeedWork.Enums;
using SeedWork.ValueObjects;
namespace IntegrationTests.Features.Bookings;

// The backstop docs/adr/0003 anticipated for a hold left 'booked' with no
// booking behind it (a process crash between HoldConfirmation.ConfirmHoldAsync
// and the Booking insert that follows it) - see
// Bookings.Jobs.ReconcileOrphanedBookedHoldsJob's own doc comment.
[Collection("Integration Tests")]
public class ReconcileOrphanedBookedHoldsJobTests(IntegrationTestWebApplicationFactory factory)
{
    private static UnitAvailabilityHold CreateBookedHold(DateTimeOffset bookedAt)
    {
        DateOnly checkIn = DateOnly.FromDateTime(bookedAt.UtcDateTime);

        return new UnitAvailabilityHold
        {
            Id = Guid.NewGuid(),
            UnitId = Guid.NewGuid(),
            Status = "booked",
            StayRange = new NpgsqlRange<DateOnly>(checkIn, true, checkIn.AddDays(2), false),
            BookedAt = bookedAt,
            TotalPrice = Money.Of(200m, Currency.KWD),
            Subtotal = 200m
        };
    }

    private async Task SeedHoldAsync(UnitAvailabilityHold hold)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        context.Add(hold);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task SeedBookingForHoldAsync(Guid holdId, Guid unitId)
    {
        Booking booking = Booking.Create(
            Guid.CreateVersion7(), unitId, holdId, null,
            "Jane Guest", "jane@example.com", null,
            DateOnly.FromDateTime(DateTime.UtcNow), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(2),
            2, Money.Of(200m, Currency.KWD), 200m, CancellationPolicy.CreateDefault());

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        context.Bookings.Add(booking);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string> GetHoldStatusAsync(Guid holdId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        UnitAvailabilityHold hold = await context.UnitAvailabilityHolds.AsNoTracking()
            .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken);
        return hold.Status;
    }

    private async Task RunJobAsync(DateTimeOffset now)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        IHoldLookup holdLookup = scope.ServiceProvider.GetRequiredService<IHoldLookup>();
        IHoldConfirmation holdConfirmation = scope.ServiceProvider.GetRequiredService<IHoldConfirmation>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        ILogger<ReconcileOrphanedBookedHoldsJob> logger = scope.ServiceProvider.GetRequiredService<ILogger<ReconcileOrphanedBookedHoldsJob>>();
        ReconcileOrphanedBookedHoldsJob job = new ReconcileOrphanedBookedHoldsJob(bookingsDb, holdLookup, holdConfirmation, timeProvider, logger);
        await job.ReconcileAsync(null!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReconcileAsync_ReleasesOrphanedBookedHold_WithNoMatchingBooking()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UnitAvailabilityHold hold = CreateBookedHold(bookedAt: now.AddMinutes(-20));
        await SeedHoldAsync(hold);

        await RunJobAsync(now);

        Assert.Equal("held", await GetHoldStatusAsync(hold.Id));
    }

    [Fact]
    public async Task ReconcileAsync_LeavesGenuinelyBookedHold_WithMatchingBooking_Untouched()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UnitAvailabilityHold hold = CreateBookedHold(bookedAt: now.AddMinutes(-20));
        await SeedHoldAsync(hold);
        await SeedBookingForHoldAsync(hold.Id, hold.UnitId);

        await RunJobAsync(now);

        Assert.Equal("booked", await GetHoldStatusAsync(hold.Id));
    }

    [Fact]
    public async Task ReconcileAsync_LeavesRecentlyBookedHold_WithinGraceWindow_Untouched()
    {
        // Booked 2 minutes ago, well inside the 10-minute grace window -
        // this is what a hold looks like mid-flight between
        // ConfirmHoldAsync and the Booking insert that normally follows it
        // within milliseconds. Must not be released just because the
        // Booking row hasn't landed yet.
        DateTimeOffset now = DateTimeOffset.UtcNow;
        UnitAvailabilityHold hold = CreateBookedHold(bookedAt: now.AddMinutes(-2));
        await SeedHoldAsync(hold);

        await RunJobAsync(now);

        Assert.Equal("booked", await GetHoldStatusAsync(hold.Id));
    }
}
