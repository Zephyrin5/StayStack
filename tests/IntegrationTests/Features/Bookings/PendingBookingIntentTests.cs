using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Features.HoldAvailability;
using Bookings.Features.ConfirmBooking;
using Bookings.Jobs;
using Catalog;
using Catalog.Contracts;
using Catalog.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Promotions.Contracts;
using SeedWork.Enums;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Json;
using Bookings.Outbox;
namespace IntegrationTests.Features.Bookings;

// Durable intents (docs/adr/0017). The redemption commits in Promotions before
// the Booking exists, so a process death between them would leave nothing to
// recover from without a marker. An intent row states the fact directly, and
// its tracked deletion on the success path is what makes the recovery job and a
// live request safe to run concurrently.
[Collection("Integration Tests")]
public class PendingBookingIntentTests(IntegrationTestWebApplicationFactory factory)
{
    // Hand-written rather than a mocking library - IntegrationTests doesn't
    // reference Moq (unlike UnitTests), matching how OutboxDeadLetterCountingTests
    // stubs its own dispatcher.
    private sealed class UnreachableHoldConfirmation : IHoldConfirmation
    {
        public Task<ConfirmedHold> ConfirmHoldAsync(Guid holdId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Availability is unreachable.");

        public Task<bool> MarkHoldPaidAsync(Guid holdId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Availability is unreachable.");

        public Task ReleaseHoldAsync(Guid holdId, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Availability is unreachable.");
    }

    /// <summary>
    ///     Delegates every call to the real IUnitLookup, running a side effect
    ///     first on GetUnitAsync. That call sits inside ConfirmBookingHandler's
    ///     try block immediately before Booking.Create - the only deterministic
    ///     seam between ConfirmHoldAsync committing and the final save, which
    ///     is exactly the window both races below need to open. Delegating
    ///     rather than stubbing matters: this same interface also serves
    ///     HoldAvailabilityHandler and PromotionRedemption, so a stub would
    ///     break unrelated setup in the same test.
    /// </summary>
    private sealed class SideEffectUnitLookup(IUnitLookup inner, Func<Task> onGetUnit) : IUnitLookup
    {
        public async Task<UnitSummary?> GetUnitAsync(Guid unitId, CancellationToken cancellationToken)
        {
            await onGetUnit();
            return await inner.GetUnitAsync(unitId, cancellationToken);
        }

        public Task<IReadOnlyDictionary<Guid, UnitSummary>> GetUnitsAsync(IEnumerable<Guid> unitIds, CancellationToken cancellationToken) =>
            inner.GetUnitsAsync(unitIds, cancellationToken);


        public Task<UnitSummary?> GetUnitIncludingArchivedAsync(Guid unitId, CancellationToken cancellationToken) =>

            inner.GetUnitIncludingArchivedAsync(unitId, cancellationToken);

        public Task<IReadOnlyList<Guid>> GetUnitIdsForHostAsync(Guid hostId, CancellationToken cancellationToken) =>
            inner.GetUnitIdsForHostAsync(hostId, cancellationToken);

        public Task<StayPricingResult?> ResolveStayPricingAsync(
            Guid unitId, DateOnly checkIn, DateOnly checkOut, CancellationToken cancellationToken) =>
            inner.ResolveStayPricingAsync(unitId, checkIn, checkOut, cancellationToken);
    }

    private HttpClient CreateClientWithSeam(Func<IServiceProvider, Task> onGetUnit) =>
        factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IUnitLookup));
            services.Remove(original);
            services.AddScoped<IUnitLookup>(sp => new SideEffectUnitLookup(
                (IUnitLookup)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!),
                () => onGetUnit(sp)));
        })).CreateClient();

    private readonly HttpClient _client = factory.CreateClient();

    private static (Property Property, Unit Unit) CreateTestUnit()
    {
        // A real Property, not a throwaway id - see CatalogSeeding.
        Property property = CatalogSeeding.CreateProperty();
        return (property, Unit.Create(
            Guid.CreateVersion7(),
            property.Id,
        LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
        2,
        100m));
    }
    private async Task<Unit> SeedUnitAsync()
    {
        (Property property, Unit unit) = CreateTestUnit();
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        // Owner first - a Unit without its Property does not resolve.
        context.Add(property);
        context.Add(unit);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return unit;
    }

    private async Task<Guid> HoldUnitAsync(Guid unitId, int dayOffset = 0)
    {
        DateOnly checkIn = CatalogSeeding.Today().AddDays(dayOffset);
        HttpResponseMessage response = await _client.PostAsJsonAsync("/api/availability/holds", new HoldAvailabilityRequest
        {
            UnitId = unitId,
            CheckIn = checkIn,
            CheckOut = checkIn.AddDays(3),
            GuestCount = 2
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        HoldAvailabilityResponse? hold = await response.Content.ReadFromJsonAsync<HoldAvailabilityResponse>(
            TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);
        return hold.HoldId;
    }

    private static ConfirmBookingRequest CreateRequest(Guid holdId) => new ConfirmBookingRequest
    {
        HoldId = holdId,
        GuestName = "Jane Guest",
        GuestEmail = "jane@example.com"
    };

    private async Task<string> GetHoldStatusAsync(Guid holdId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        UnitAvailabilityHold hold = await context.UnitAvailabilityHolds.AsNoTracking()
            .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken);
        return hold.Status;
    }

    private async Task<PendingBookingIntent?> GetIntentAsync(Guid holdId)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext context = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        return await context.PendingBookingIntents.AsNoTracking()
            .SingleOrDefaultAsync(i => i.HoldId == holdId, TestContext.Current.CancellationToken);
    }

    private async Task RunReconcileAsync(DateTimeOffset now)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        ReconcileOrphanedBookingIntentsJob job = new ReconcileOrphanedBookingIntentsJob(
            bookingsDb,
            scope.ServiceProvider.GetRequiredService<IHoldConfirmation>(),
            scope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>(),
            timeProvider,
            scope.ServiceProvider.GetRequiredService<ILogger<ReconcileOrphanedBookingIntentsJob>>());

        await job.ReconcileAsync(null!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ReconcileAsync_ReleasesHoldAndDeletesIntent_ForAnAbandonedConfirmation()
    {
        // The crash this whole design exists for: the hold is 'booked' and no
        // Booking will ever follow. Seeded directly, since a process death
        // between ConfirmHoldAsync and the Booking insert can't be provoked
        // through HTTP.
        Unit unit = await SeedUnitAsync();
        Guid holdId = await HoldUnitAsync(unit.Id);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppBookingsDbContext availabilityDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            UnitAvailabilityHold hold = await availabilityDb.UnitAvailabilityHolds
                .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken);
            hold.Status = "booked";
            hold.BookedAt = now.AddMinutes(-20);
            await availabilityDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            bookingsDb.PendingBookingIntents.Add(new PendingBookingIntent
            {
                Id = Guid.CreateVersion7(),
                HoldId = holdId,
                CreatedAt = now.AddMinutes(-20)
            });
            await bookingsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await RunReconcileAsync(now);

        Assert.Equal("held", await GetHoldStatusAsync(holdId));
        Assert.Null(await GetIntentAsync(holdId));
    }

    [Fact]
    public async Task ReconcileAsync_LeavesAnIntentInsideTheGraceWindow_Untouched()
    {
        // A request still legitimately in flight. The grace window is not what
        // makes the design safe (the success-path delete is), but the job
        // still shouldn't go looking for trouble.
        Unit unit = await SeedUnitAsync();
        Guid holdId = await HoldUnitAsync(unit.Id);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppBookingsDbContext availabilityDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            UnitAvailabilityHold hold = await availabilityDb.UnitAvailabilityHolds
                .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken);
            hold.Status = "booked";
            hold.BookedAt = now.AddMinutes(-1);
            await availabilityDb.SaveChangesAsync(TestContext.Current.CancellationToken);

            AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            bookingsDb.PendingBookingIntents.Add(new PendingBookingIntent
            {
                Id = Guid.CreateVersion7(),
                HoldId = holdId,
                CreatedAt = now.AddMinutes(-1)
            });
            await bookingsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        await RunReconcileAsync(now);

        Assert.Equal("booked", await GetHoldStatusAsync(holdId));
        Assert.NotNull(await GetIntentAsync(holdId));
    }

    [Fact]
    public async Task ReconcileAsync_WhenTheReleaseFails_LeavesTheIntentForTheNextRun()
    {
        // The claim must share a fate with the work it authorises. If the
        // claim committed first (an autocommitting UPDATE ... RETURNING, say),
        // a failure here would leave the hold stuck 'booked' with its intent
        // already gone - the exact bug class this design removes, one layer
        // down.
        Unit unit = await SeedUnitAsync();
        Guid holdId = await HoldUnitAsync(unit.Id);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid intentId = Guid.CreateVersion7();

        using (IServiceScope seedScope = factory.Services.CreateScope())
        {
            AppBookingsDbContext bookingsDb = seedScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            bookingsDb.PendingBookingIntents.Add(new PendingBookingIntent
            {
                Id = intentId,
                HoldId = holdId,
                CreatedAt = now.AddMinutes(-20)
            });
            await bookingsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        using IServiceScope scope = factory.Services.CreateScope();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        ReconcileOrphanedBookingIntentsJob job = new ReconcileOrphanedBookingIntentsJob(
            scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>(),
            new UnreachableHoldConfirmation(),
            scope.ServiceProvider.GetRequiredService<BookingsOutboxDispatcher>(),
            timeProvider,
            scope.ServiceProvider.GetRequiredService<ILogger<ReconcileOrphanedBookingIntentsJob>>());

        // The run completes rather than propagating: a failed compensation leaves
        // the intent behind for the next run, which is the assertion below.
        // Letting the exception escape would abandon every candidate queued
        // behind this one, and nothing it can throw is classified transient,
        // so EnableRetryOnFailure would not absorb it either.
        await job.ReconcileAsync(null!, TestContext.Current.CancellationToken);

        Assert.NotNull(await GetIntentAsync(holdId));
    }
}
