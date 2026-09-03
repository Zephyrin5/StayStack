using Availability;
using Availability.Entities;
using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Jobs;
using Catalog;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using NpgsqlTypes;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using Availability.Contracts;
using Promotions.Contracts;
namespace IntegrationTests.Features.Bookings;

// Confirming a checkout takes a unit off the market: the hold moves to
// 'pending_payment' and goes on blocking its range through the exclusion
// constraint, which has no status predicate. Nothing used to give that back.
// An anonymous caller could hold a unit, submit the checkout form, and repeat
// - each submission turning a 15-minute hold into a permanent one, escaping
// the per-client cap (which counts 'held' only) and never paying. These pin
// the deadline that makes the claim finite and the job that enforces it.
[Collection("Integration Tests")]
public class ExpireUnpaidBookingsTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly List<Property> _pendingProperties = [];

    private Unit CreateTestUnit()
    {
        Property property = CatalogSeeding.CreateProperty();
        _pendingProperties.Add(property);

        return Unit.Create(
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
            2,
            100);
    }

    private async Task SeedCatalogAsync(params object[] entities)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        context.AddRange(_pendingProperties);
        _pendingProperties.Clear();
        context.AddRange(entities);
        await context.SaveChangesAsync();
    }

    private async Task<Guid> SeedHoldAsync(Guid unitId, DateOnly checkIn, DateOnly checkOut, string status)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();

        UnitAvailabilityHold hold = new UnitAvailabilityHold
        {
            Id = Guid.CreateVersion7(),
            UnitId = unitId,
            StayRange = new NpgsqlRange<DateOnly>(checkIn, true, checkOut, false),
            Status = status,
            GuestCount = 2,
            CreatedAt = DateTimeOffset.UtcNow,
            TotalPrice = Money.Of(100m, Currency.KWD),
            Subtotal = 100m
        };

        context.UnitAvailabilityHolds.Add(hold);
        await context.SaveChangesAsync();
        return hold.Id;
    }

    private async Task<Guid> SeedBookingAsync(Guid unitId, Guid holdId, DateTimeOffset paymentDueAt)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        Booking booking = Booking.Create(
            Guid.CreateVersion7(), unitId, holdId, null,
            "Jane Guest", "jane@example.com", null,
            DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(10)),
            DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(12)),
            2, Money.Of(200m, Currency.KWD), Money.Of(200m, Currency.KWD),
            CancellationPolicy.CreateDefault(), "Asia/Kuwait", paymentDueAt);

        context.Bookings.Add(booking);
        await context.SaveChangesAsync();
        return booking.Id;
    }

    private static ExpireUnpaidBookingsJob CreateJob(IServiceScope scope, TimeProvider timeProvider)
    {
        return new ExpireUnpaidBookingsJob(
            scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>(),
            scope.ServiceProvider.GetRequiredService<IHoldConfirmation>(),
            scope.ServiceProvider.GetRequiredService<IPromotionRedemption>(),
            timeProvider,
            NullLogger<ExpireUnpaidBookingsJob>.Instance);
    }

    private static FakeTimeProvider At(DateTimeOffset instant)
    {
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(instant);
        return timeProvider;
    }

    private async Task<string> GetHoldStatusAsync(Guid holdId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
        UnitAvailabilityHold hold = await context.UnitAvailabilityHolds.AsNoTracking()
            .SingleAsync(h => h.Id == holdId);
        return hold.Status;
    }

    private async Task<BookingStatus> GetBookingStatusAsync(Guid bookingId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Booking booking = await context.Bookings.AsNoTracking().SingleAsync(b => b.Id == bookingId);
        return booking.BookingStatus;
    }

    [Fact]
    public async Task AnOverdueUnpaidBooking_IsCancelled_AndItsUnitReturnedToInventory()
    {
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(40));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");
        Guid bookingId = await SeedBookingAsync(unit.Id, holdId, now.AddMinutes(-1));

        using IServiceScope scope = factory.Services.CreateScope();
        ExpireUnpaidBookingsJob job = CreateJob(scope, At(now));

        // Act
        await job.ExpireAsync(null!, TestContext.Current.CancellationToken);

        // Assert - both halves, which is the whole reason this job lives in
        // Bookings rather than Availability. Releasing the hold alone would
        // leave the guest holding a booking with no inventory behind it;
        // cancelling alone would leave the range blocked forever.
        Assert.Equal(BookingStatus.Cancelled, await GetBookingStatusAsync(bookingId));

        // 'held' with its timer reset, not deleted - the ordinary expiry
        // sweep reclaims it from here.
        Assert.Equal("held", await GetHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task ABookingStillWithinItsPaymentWindow_IsLeftAlone()
    {
        // The other half: a job that expired everything would pass the test
        // above while breaking every checkout in progress.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(41));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");
        Guid bookingId = await SeedBookingAsync(unit.Id, holdId, now.AddMinutes(15));

        using IServiceScope scope = factory.Services.CreateScope();
        ExpireUnpaidBookingsJob job = CreateJob(scope, At(now));

        // Act
        await job.ExpireAsync(null!, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BookingStatus.Pending, await GetBookingStatusAsync(bookingId));
        Assert.Equal("pending_payment", await GetHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task APaidBooking_IsNeverExpired_EvenIfItsDeadlineWouldHavePassed()
    {
        // The dangerous direction. A payment that resolves near the deadline
        // leaves a Confirmed booking whose hold is 'booked'; expiring it
        // would take a paid stay away from a guest and hand the range to
        // somebody else. Both the status filter and the null PaymentDueAt
        // that Confirm() leaves behind have to hold for that not to happen.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(42));
        DateTimeOffset now = DateTimeOffset.UtcNow;

        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");
        Guid bookingId = await SeedBookingAsync(unit.Id, holdId, now.AddMinutes(-5));

        using (IServiceScope paymentScope = factory.Services.CreateScope())
        {
            IBookingPaymentConfirmation paymentConfirmation =
                paymentScope.ServiceProvider.GetRequiredService<IBookingPaymentConfirmation>();
            Assert.True(await paymentConfirmation.ConfirmPaymentAsync(bookingId, TestContext.Current.CancellationToken));
        }

        // Paying moves the hold the rest of the way, which is what makes
        // 'booked' a reachable state rather than a dead one.
        Assert.Equal("booked", await GetHoldStatusAsync(holdId));

        using IServiceScope scope = factory.Services.CreateScope();
        ExpireUnpaidBookingsJob job = CreateJob(scope, At(now));

        // Act
        await job.ExpireAsync(null!, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal(BookingStatus.Confirmed, await GetBookingStatusAsync(bookingId));
        Assert.Equal("booked", await GetHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task ReleasingAClaimedHold_Works_WhichIsWhatEveryCompensationDependsOn()
    {
        // Guards the trap this change could most easily have introduced.
        // ReleaseHoldAsync used to match WHERE status = 'booked'. Once
        // checkout began producing 'pending_payment' instead, that predicate
        // would have matched nothing - turning every compensating release
        // (ConfirmBookingHandler's catch blocks, the promo-rejection branch,
        // ReconcileOrphanedBookingIntentsJob, CancelBookingHandler, this
        // expiry job) into a silent zero-row no-op that strands the hold.
        // Nothing about that failure is loud, so it gets its own test.
        // Arrange
        Unit unit = CreateTestUnit();
        await SeedCatalogAsync(unit);

        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow.Date.AddDays(43));
        Guid holdId = await SeedHoldAsync(unit.Id, checkIn, checkIn.AddDays(2), "pending_payment");

        using IServiceScope scope = factory.Services.CreateScope();
        IHoldConfirmation holdConfirmation = scope.ServiceProvider.GetRequiredService<IHoldConfirmation>();

        // Act
        await holdConfirmation.ReleaseHoldAsync(holdId, TestContext.Current.CancellationToken);

        // Assert
        Assert.Equal("held", await GetHoldStatusAsync(holdId));
    }
}
