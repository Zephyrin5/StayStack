using Availability;
using Availability.Contracts;
using Availability.Entities;
using Availability.Features.HoldAvailability;
using Bookings;
using Bookings.Entities;
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
namespace IntegrationTests.Features.Bookings;

// The durable-intent redesign (docs/adr/0017). ConfirmHoldAsync commits to
// Availability's own database before anything exists in Bookings, so a process
// death on the next line used to leave nothing anywhere to recover from -
// covered only by a job that asked Availability for candidates and joined them
// against Bookings in memory. An intent row states the fact directly, and its
// deletion on the success path is what makes the recovery job and a live
// request safe to run concurrently.
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

    private static Unit CreateTestUnit() => Unit.Create(
        Guid.CreateVersion7(),
        LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
        2,
        100m);

    private async Task<Unit> SeedUnitAsync()
    {
        Unit unit = CreateTestUnit();
        using IServiceScope scope = factory.Services.CreateScope();
        AppCatalogDbContext context = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
        context.Add(unit);
        await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        return unit;
    }

    private async Task<Guid> HoldUnitAsync(Guid unitId, int dayOffset = 0)
    {
        DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(dayOffset);
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
        AppAvailabilityDbContext context = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
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
            scope.ServiceProvider.GetRequiredService<IPromotionRedemption>(),
            timeProvider,
            scope.ServiceProvider.GetRequiredService<ILogger<ReconcileOrphanedBookingIntentsJob>>());

        await job.ReconcileAsync(null!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConfirmBooking_OnSuccess_LeavesNoIntentBehind()
    {
        Unit unit = await SeedUnitAsync();
        Guid holdId = await HoldUnitAsync(unit.Id);

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/bookings", CreateRequest(holdId), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        // The row exists only while the work is in flight - a surviving one
        // would be reconciled later and release the hold under a live booking.
        Assert.Null(await GetIntentAsync(holdId));
    }

    [Fact]
    public async Task ConfirmBooking_WhenHoldAlreadyConsumed_LeavesNoIntentBehind()
    {
        // The ordinary-exception path that has no compensation to run:
        // ConfirmHoldAsync rejects an already-booked hold outright. Without
        // the try/catch around that first call, this request's intent would
        // sit until the grace period elapsed and the job released a hold that
        // nothing was wrong with.
        Unit unit = await SeedUnitAsync();
        Guid holdId = await HoldUnitAsync(unit.Id);

        Assert.Equal(HttpStatusCode.OK, (await _client.PostAsJsonAsync(
            "/api/bookings", CreateRequest(holdId), TestContext.Current.CancellationToken)).StatusCode);

        HttpResponseMessage second = await _client.PostAsJsonAsync(
            "/api/bookings", CreateRequest(holdId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Null(await GetIntentAsync(holdId));
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
            AppAvailabilityDbContext availabilityDb = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
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
            AppAvailabilityDbContext availabilityDb = scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
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
    public async Task ConfirmBooking_WhileAnotherConfirmationHoldsTheIntent_Returns409()
    {
        // The unique index on hold_id is what makes dropping the old
        // cross-module join safe: without it a second intent for the same hold
        // would survive, and the job would later release a hold out from under
        // a live request.
        Unit unit = await SeedUnitAsync();
        Guid holdId = await HoldUnitAsync(unit.Id);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            bookingsDb.PendingBookingIntents.Add(new PendingBookingIntent
            {
                Id = Guid.CreateVersion7(),
                HoldId = holdId,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await bookingsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        HttpResponseMessage response = await _client.PostAsJsonAsync(
            "/api/bookings", CreateRequest(holdId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        // The hold is untouched - the request never got as far as
        // ConfirmHoldAsync, so the in-flight confirmation still owns it.
        Assert.Equal("held", await GetHoldStatusAsync(holdId));
    }

    [Fact]
    public async Task ConfirmBooking_WhenTheJobReconcilesMidRequest_Returns409_AndWritesNoBooking()
    {
        // The race the whole design turns on. Nothing in ConfirmBookingHandler
        // re-validates the hold after ConfirmHoldAsync, so a job firing
        // mid-request releases a genuinely-'booked' hold while the request
        // goes on to insert a confirmed Booking anyway - leaving that booking
        // backed by a hold anyone can re-book. No grace period fixes this;
        // only the success-path delete does, because it rides in the same
        // transaction as the Booking insert and EF asserts its affected-row
        // count.
        Unit unit = await SeedUnitAsync();
        Guid holdId = await HoldUnitAsync(unit.Id, dayOffset: 80);

        HttpClient client = CreateClientWithSeam(async _ =>
        {
            // Stands in for the reconcile job winning: the intent is gone by
            // the time the handler's final save runs. A second scope, because
            // the handler's own DbContext must not see this coming.
            using IServiceScope scope = factory.Services.CreateScope();
            AppBookingsDbContext db = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
            await db.PendingBookingIntents.Where(i => i.HoldId == holdId).ExecuteDeleteAsync();
        });

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/bookings", CreateRequest(holdId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Assert.False(
            await bookingsDb.Bookings.AnyAsync(b => b.HoldId == holdId, TestContext.Current.CancellationToken),
            "A Booking was written even though the intent had already been reconciled - the structural guarantee is not holding.");
    }

    [Fact]
    public async Task ConfirmBooking_WhenTheBookingAlreadyCommitted_ReturnsSuccess_AndCompensatesNothing()
    {
        // The committed-but-unacknowledged retry. SaveChangesAsync runs under
        // EnableRetryOnFailure, and an execution strategy cannot tell a failed
        // transaction from one that committed and lost its acknowledgement -
        // it re-runs the batch, which then fails against its own rows.
        // Simulated by committing a Booking under the intent's own Id (which
        // *is* the bookingId) just before the handler's save.
        //
        // Without the verify step the handler would compensate a live booking:
        // hold released back to immediately-re-bookable, redemption reversed,
        // and a 500 for a booking that actually succeeded.
        Unit unit = await SeedUnitAsync();
        Guid holdId = await HoldUnitAsync(unit.Id, dayOffset: 100);

        HttpClient client = CreateClientWithSeam(async _ =>
        {
            using IServiceScope scope = factory.Services.CreateScope();
            AppBookingsDbContext db = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

            PendingBookingIntent intent = await db.PendingBookingIntents.AsNoTracking()
                .SingleAsync(i => i.HoldId == holdId);

            // A deliberately distinguishable guest count and price, so the
            // assertions below can tell "returned the already-committed row"
            // apart from "created its own" - otherwise this test would pass
            // just as happily if the seam never fired.
            DateOnly checkIn = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(100);
            db.Bookings.Add(Booking.Create(
                intent.Id, unit.Id, holdId, null,
                "Committed By Retry", "jane@example.com", null,
                checkIn, checkIn.AddDays(3), 1,
                Money.Of(999m, Currency.KWD), 999m, CancellationPolicy.CreateDefault()));
            await db.SaveChangesAsync();
        });

        HttpResponseMessage response = await client.PostAsJsonAsync(
            "/api/bookings", CreateRequest(holdId), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ConfirmBookingResponse? result = await response.Content.ReadFromJsonAsync<ConfirmBookingResponse>(
            TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(result);

        // The already-committed row's own figures, not a freshly-created
        // booking's - this is what proves the verify query drove the outcome.
        Assert.Equal(999m, result.TotalPrice);

        // The hold stays 'booked' - compensation must not have run.
        Assert.Equal("booked", await GetHoldStatusAsync(holdId));

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        Booking persisted = await bookingsDb.Bookings.AsNoTracking()
            .SingleAsync(b => b.HoldId == holdId, TestContext.Current.CancellationToken);
        Assert.Equal("Committed By Retry", persisted.GuestName);
        Assert.Null(await GetIntentAsync(holdId));
        Assert.False(
            await bookingsDb.BookingsOutboxMessages.AnyAsync(
                // global:: qualified - this file's own namespace
                // (IntegrationTests.Features.Bookings) otherwise shadows the
                // Bookings module's, the same collision UserManagementTests
                // documents at its namespace declaration.
                m => m.Type == nameof(global::Bookings.Outbox.ReleaseHoldOutboxMessage) && m.Payload.Contains(holdId.ToString()),
                TestContext.Current.CancellationToken),
            "A compensating release was queued for a booking that actually committed.");
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
            scope.ServiceProvider.GetRequiredService<IPromotionRedemption>(),
            timeProvider,
            scope.ServiceProvider.GetRequiredService<ILogger<ReconcileOrphanedBookingIntentsJob>>());

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
            job.ReconcileAsync(null!, TestContext.Current.CancellationToken));

        Assert.NotNull(await GetIntentAsync(holdId));
    }

    [Fact]
    public async Task ReconcileAsync_RunAlongsideTheSupersededJob_IsSafeInEitherOrder()
    {
        // docs/adr/0017 ships the new job for one release *alongside* the two
        // it replaces, because an orphan created before pending_booking_intents
        // existed has no intent row for the new job to find. That overlap is
        // the load-bearing premise of the deployment plan, so it gets a test
        // rather than a sentence: both jobs act on the same orphan, in either
        // order, without erroring or fighting.
        Unit unit = await SeedUnitAsync();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        foreach (bool newJobFirst in new[] { true, false })
        {
            Guid holdId = await HoldUnitAsync(unit.Id, dayOffset: newJobFirst ? 40 : 60);

            using (IServiceScope seedScope = factory.Services.CreateScope())
            {
                AppAvailabilityDbContext availabilityDb = seedScope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>();
                UnitAvailabilityHold hold = await availabilityDb.UnitAvailabilityHolds
                    .SingleAsync(h => h.Id == holdId, TestContext.Current.CancellationToken);
                hold.Status = "booked";
                hold.BookedAt = now.AddMinutes(-20);
                await availabilityDb.SaveChangesAsync(TestContext.Current.CancellationToken);

                AppBookingsDbContext bookingsDb = seedScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
                bookingsDb.PendingBookingIntents.Add(new PendingBookingIntent
                {
                    Id = Guid.CreateVersion7(),
                    HoldId = holdId,
                    CreatedAt = now.AddMinutes(-20)
                });
                await bookingsDb.SaveChangesAsync(TestContext.Current.CancellationToken);
            }

            if (newJobFirst)
            {
                await RunReconcileAsync(now);
                await RunSupersededHoldsJobAsync(now);
            }
            else
            {
                await RunSupersededHoldsJobAsync(now);
                await RunReconcileAsync(now);
            }

            Assert.Equal("held", await GetHoldStatusAsync(holdId));
            Assert.Null(await GetIntentAsync(holdId));
        }
    }

    private async Task RunSupersededHoldsJobAsync(DateTimeOffset now)
    {
        using IServiceScope scope = factory.Services.CreateScope();
        FakeTimeProvider timeProvider = new FakeTimeProvider();
        timeProvider.SetUtcNow(now);

        ReconcileOrphanedBookedHoldsJob job = new ReconcileOrphanedBookedHoldsJob(
            scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>(),
            scope.ServiceProvider.GetRequiredService<IHoldLookup>(),
            scope.ServiceProvider.GetRequiredService<IHoldConfirmation>(),
            timeProvider,
            scope.ServiceProvider.GetRequiredService<ILogger<ReconcileOrphanedBookedHoldsJob>>());

        await job.ReconcileAsync(null!, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ConfirmBooking_ConcurrentRequestsForTheSameHold_ExactlyOneSucceeds()
    {
        Unit unit = await SeedUnitAsync();
        Guid holdId = await HoldUnitAsync(unit.Id);

        const int concurrentRequests = 6;
        Task<HttpResponseMessage>[] tasks =
        [
            .. Enumerable.Range(0, concurrentRequests)
                .Select(_ => factory.CreateClient().PostAsJsonAsync(
                    "/api/bookings", CreateRequest(holdId), TestContext.Current.CancellationToken))
        ];

        HttpResponseMessage[] responses = await Task.WhenAll(tasks);

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.OK));

        // The losers are rejected, never silently duplicated. Both shapes are
        // correct: 409 when the intent insert lost the race, 404 when this one
        // got far enough to find the hold already consumed.
        Assert.All(
            responses.Where(r => r.StatusCode != HttpStatusCode.OK),
            r => Assert.Contains(r.StatusCode, new[] { HttpStatusCode.Conflict, HttpStatusCode.NotFound }));

        using IServiceScope scope = factory.Services.CreateScope();
        AppBookingsDbContext bookingsDb = scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();
        int bookingCount = await bookingsDb.Bookings.CountAsync(
            b => b.HoldId == holdId, TestContext.Current.CancellationToken);
        Assert.Equal(1, bookingCount);
        Assert.Null(await GetIntentAsync(holdId));
    }
}
