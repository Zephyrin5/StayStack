using Bookings;
using Bookings.Contracts;
using Bookings.Entities;
using Bookings.Features.ConfirmBooking;
using Bookings.Features.HoldAvailability;
using Catalog;
using Catalog.Contracts;
using Catalog.Entities;
using Catalog.Features.CreateUnit;
using Identity.Features.SignIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Catalog;

// Archiving checked "no active bookings or holds" and then archived, with
// nothing spanning the two. A hold taken in that gap left a unit archived with
// live inventory against it. Existing tests only cover "a booking already
// exists", which the check catches on its own.
//
// Every test here is driven from a gate rather than from two requests fired at
// once. That is not a stylistic preference. Racing real requests leaves the
// interleaving to whoever wins, so the test passes against broken code whenever
// it happens not to race - and a green race test is indistinguishable from a
// green correct one. Pausing the archive at a known point makes the interleaving
// the test's to choose, and all three were confirmed to fail with the locks
// removed.
[Collection("Integration Tests")]
public class ArchivalRaceTests(IntegrationTestWebApplicationFactory factory)
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
            100m);
    }

    // Parks the archiving transaction at the one point that reproduces the
    // defect: this unit has passed *every* check and the archive has not
    // committed.
    //
    // It decorates the hold lookup rather than IUnitArchivalGuard, and that is
    // load-bearing rather than arbitrary. The pause has to leave the archive
    // with *both* checks behind it; parking it between them means the check
    // that has not run yet sees whatever the test just did and refuses
    // correctly, with or without a lock - so the test passes against the broken
    // code. The first draft did exactly that.
    //
    // UnitArchival checks holds first and bookings second, so the hold lookup
    // is the earlier of the two and this decorator alone would park the archive
    // in the middle. What saves it is that these three tests race the *hold*
    // lookup's own subject: each releases only after the thing it is racing has
    // finished, and the booking check that runs afterwards has no opinion about
    // a hold. If a test is ever added here whose racing action creates a
    // booking, it must use the first-check gate below instead.
    private sealed class PauseAfterChecking(
        IUnitAvailabilityLookup inner, Guid unitId, TaskCompletionSource gate, TaskCompletionSource reached)
        : IUnitAvailabilityLookup
    {
        public async Task<bool> HasActiveHoldForUnitAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
        {
            bool result = await inner.HasActiveHoldForUnitAsync(id, now, cancellationToken);

            if (id == unitId)
            {
                reached.TrySetResult();
                await gate.Task;
            }

            return result;
        }

        public Task<IReadOnlyList<ActiveHoldRange>> GetActiveHoldRangesAsync(
            Guid unitId, DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.GetActiveHoldRangesAsync(unitId, from, to, now, cancellationToken);

        public Task<IReadOnlySet<Guid>> GetBlockedUnitIdsAsync(
            DateOnly checkIn, DateOnly checkOut, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.GetBlockedUnitIdsAsync(checkIn, checkOut, now, cancellationToken);
    }

    // Fires once, on whichever of UnitArchival's two checks runs first.
    //
    // Deliberately order-agnostic. The property under test is "a checkout that
    // completes while an archive is deciding cannot slip through", and that has
    // to hold whichever check is written first - a test that hard-codes the
    // current order would go green the moment someone reorders the method,
    // which is the exact edit the ordering comment in UnitArchival exists to
    // stop.
    private sealed class FirstCheckGate(Guid unitId)
    {
        private int _armed = 1;

        public TaskCompletionSource Gate { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Reached { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task PauseIfFirstAsync(Guid id)
        {
            if (id != unitId || Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return;
            }

            Reached.TrySetResult();
            await Gate.Task;
        }
    }

    private sealed class PauseOnHoldCheck(IUnitAvailabilityLookup inner, FirstCheckGate gate) : IUnitAvailabilityLookup
    {
        public async Task<bool> HasActiveHoldForUnitAsync(Guid id, DateTimeOffset now, CancellationToken cancellationToken)
        {
            bool result = await inner.HasActiveHoldForUnitAsync(id, now, cancellationToken);
            await gate.PauseIfFirstAsync(id);
            return result;
        }

        public Task<IReadOnlyList<ActiveHoldRange>> GetActiveHoldRangesAsync(
            Guid unitId, DateOnly from, DateOnly to, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.GetActiveHoldRangesAsync(unitId, from, to, now, cancellationToken);

        public Task<IReadOnlySet<Guid>> GetBlockedUnitIdsAsync(
            DateOnly checkIn, DateOnly checkOut, DateTimeOffset now, CancellationToken cancellationToken) =>
            inner.GetBlockedUnitIdsAsync(checkIn, checkOut, now, cancellationToken);
    }

    private sealed class PauseOnBookingCheck(IUnitArchivalGuard inner, FirstCheckGate gate) : IUnitArchivalGuard
    {
        public async Task<bool> HasActiveBookingForUnitAsync(Guid id, DateOnly today, CancellationToken cancellationToken)
        {
            bool result = await inner.HasActiveBookingForUnitAsync(id, today, cancellationToken);
            await gate.PauseIfFirstAsync(id);
            return result;
        }
    }

    private HttpClient ClientPausingAtTheFirstCheck(FirstCheckGate gate) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor holds = services.Single(d => d.ServiceType == typeof(IUnitAvailabilityLookup));
                services.Remove(holds);
                services.AddScoped<IUnitAvailabilityLookup>(sp => new PauseOnHoldCheck(
                    (IUnitAvailabilityLookup)ActivatorUtilities.CreateInstance(sp, holds.ImplementationType!), gate));

                ServiceDescriptor bookings = services.Single(d => d.ServiceType == typeof(IUnitArchivalGuard));
                services.Remove(bookings);
                services.AddScoped<IUnitArchivalGuard>(sp => new PauseOnBookingCheck(
                    (IUnitArchivalGuard)ActivatorUtilities.CreateInstance(sp, bookings.ImplementationType!), gate));
            })).CreateClient();

    private HttpClient ClientPausingTheArchiveOn(Guid unitId, TaskCompletionSource gate, TaskCompletionSource reached) =>
        factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                ServiceDescriptor original = services.Single(d => d.ServiceType == typeof(IUnitAvailabilityLookup));
                services.Remove(original);
                services.AddScoped<IUnitAvailabilityLookup>(sp => new PauseAfterChecking(
                    (IUnitAvailabilityLookup)ActivatorUtilities.CreateInstance(sp, original.ImplementationType!),
                    unitId, gate, reached));
            })).CreateClient();

    [Fact]
    public async Task AHoldTakenWhileArchivalIsDecidingIsNotLost()
    {
        Unit unit = CreateTestUnit();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
            catalog.AddRange(_pendingProperties);
            catalog.Add(unit);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        string adminToken = await SignInAsAdministratorAsync();

        (TaskCompletionSource gate, TaskCompletionSource reached) = NewGate();
        HttpClient archiveClient = ClientPausingTheArchiveOn(unit.Id, gate, reached);

        // The archive has passed its "no active bookings or holds" checks and
        // has not committed. This is the whole window, held open.
        Task<HttpResponseMessage> archive = ArchiveUnitAsync(archiveClient, unit.Id, adminToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        DateOnly checkIn = CatalogSeeding.Today().AddDays(60);

        Task<HttpResponseMessage> hold = factory.CreateClient().PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken);

        // Bounded, and nothing is asserted about it. Under the lock the hold is
        // blocked here and finishes after the archive commits; without it, it
        // finishes now. Either way the assertions below are what decides.
        await Task.WhenAny(hold, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        gate.SetResult();

        HttpResponseMessage[] responses = await Task.WhenAll(hold, archive);
        await AssertNoArchivedUnitHoldsInventoryAsync(unit.Id, responses[0], responses[1]);
    }

    [Fact]
    public async Task AHoldTakenWhileAPropertyArchiveIsDeciding_IsNotLost()
    {
        // The unit path was fixed and the property path was not, which is the
        // more dangerous of the two: it guards several units in a loop, so the
        // gap between the first unit's check and the last unit's archive is as
        // wide as the loop is long.
        //
        // And it had no explicit transaction at all - which with an advisory
        // lock is not a weaker guard but a useless one, since each lock would
        // be released before the next unit was even checked.
        Property property = CatalogSeeding.CreateProperty();

        List<Unit> units = Enumerable.Range(0, 3)
            .Select(_ => Unit.Create(
                property.Id,
                LocalizedText.Create(new Dictionary<string, string> { { "en", "Standard Room" } }, "en"),
                2,
                100m))
            .ToList();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
            catalog.Add(property);
            catalog.AddRange(units);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        string adminToken = await SignInAsAdministratorAsync();

        // The last unit in the cascade. Pausing on it means every unit has now
        // passed its guard, so nothing is left to catch a hold arriving next
        // except the locks still being held.
        Unit target = units.OrderBy(unit => unit.Id).Last();

        (TaskCompletionSource gate, TaskCompletionSource reached) = NewGate();
        HttpClient archiveClient = ClientPausingTheArchiveOn(target.Id, gate, reached);

        Task<HttpResponseMessage> archive = ArchivePropertyAsync(archiveClient, property.Id, adminToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        DateOnly checkIn = CatalogSeeding.Today().AddDays(70);

        Task<HttpResponseMessage> hold = factory.CreateClient().PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = target.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken);

        await Task.WhenAny(hold, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        gate.SetResult();

        HttpResponseMessage[] responses = await Task.WhenAll(hold, archive);
        await AssertNoArchivedUnitHoldsInventoryAsync(target.Id, responses[0], responses[1]);
    }

    [Fact]
    public async Task AUnitCreatedWhileItsPropertyIsBeingArchived_DoesNotSurviveTheProperty()
    {
        // The second race, and the one no per-unit lock can reach. Archiving a
        // property finds its units by reading them; a unit created after that
        // read is neither checked nor archived, and ends up live under an
        // archived property - the orphan state UnitLookup throws
        // OrphanedUnitException for, reached without anyone doing anything
        // wrong.
        //
        // There is no row to lock, because the row does not exist yet. The
        // thing both sides can agree on is the property.
        Property property = CatalogSeeding.CreateProperty();

        // One unit seeded, purely so the archive's guard loop runs at all and
        // there is somewhere to pause it. An empty property archives with no
        // round trips and cannot be interleaved with.
        Unit existing = Unit.Create(
            property.Id,
            LocalizedText.Create(new Dictionary<string, string> { { "en", "First Room" } }, "en"),
            2,
            100m);

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
            catalog.Add(property);
            catalog.Add(existing);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        string adminToken = await SignInAsAdministratorAsync();

        (TaskCompletionSource gate, TaskCompletionSource reached) = NewGate();
        HttpClient archiveClient = ClientPausingTheArchiveOn(existing.Id, gate, reached);

        Task<HttpResponseMessage> archive = ArchivePropertyAsync(archiveClient, property.Id, adminToken);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The creation starts squarely inside the archive's window. With the
        // property lock it blocks until the archive commits and then finds no
        // property to attach to; without it, it commits straight through.
        Task<HttpResponseMessage> create = CreateUnitAsync(property.Id, adminToken);

        await Task.WhenAny(create, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        gate.SetResult();

        HttpResponseMessage[] responses = await Task.WhenAll(create, archive);

        bool createSucceeded = responses[0].StatusCode == HttpStatusCode.OK;
        bool archiveSucceeded = responses[1].StatusCode == HttpStatusCode.OK;

        using IServiceScope assertScope = factory.Services.CreateScope();
        AppCatalogDbContext catalogDb = assertScope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();

        // The soft-delete query filter is what "archived" means here: an
        // archived row is simply not found.
        bool propertyIsArchived = !await catalogDb.Properties.AsNoTracking()
            .AnyAsync(p => p.Id == property.Id, TestContext.Current.CancellationToken);

        bool liveUnitRemains = await catalogDb.Units.AsNoTracking()
            .AnyAsync(u => u.PropertyId == property.Id, TestContext.Current.CancellationToken);

        Assert.False(
            propertyIsArchived && liveUnitRemains,
            $"A unit outlived the property it belongs to (create {createSucceeded}, archive {archiveSucceeded}). " +
            "That is the orphan UnitLookup throws OrphanedUnitException for.");

        // The run is only meaningful if both sides actually did something - two
        // failures would satisfy the assertion above while testing nothing.
        Assert.True(createSucceeded || archiveSucceeded);
    }

    private async Task AssertNoArchivedUnitHoldsInventoryAsync(
        Guid unitId, HttpResponseMessage holdResponse, HttpResponseMessage archiveResponse)
    {
        bool holdSucceeded = holdResponse.StatusCode == HttpStatusCode.OK;
        bool archiveSucceeded = archiveResponse.StatusCode == HttpStatusCode.OK;

        using IServiceScope assertScope = factory.Services.CreateScope();

        bool unitIsArchived = !await assertScope.ServiceProvider.GetRequiredService<AppCatalogDbContext>()
            .Units.AsNoTracking()
            .AnyAsync(u => u.Id == unitId, TestContext.Current.CancellationToken);

        bool holdExists = await assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>()
            .UnitAvailabilityHolds.AsNoTracking()
            .AnyAsync(h => h.UnitId == unitId && h.Status == "held", TestContext.Current.CancellationToken);

        Assert.False(
            unitIsArchived && holdExists,
            $"The unit was archived with a live hold against it (hold {holdSucceeded}, archive {archiveSucceeded}). " +
            "The guard and the archive have to be one decision, not two.");

        // And the run is only meaningful if both sides actually did something -
        // two failures would satisfy the assertion above while testing nothing
        // at all.
        Assert.True(holdSucceeded || archiveSucceeded);
    }

    private static (TaskCompletionSource Gate, TaskCompletionSource Reached) NewGate() =>
        (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously),
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

    private async Task<string> SignInAsAdministratorAsync()
    {
        HttpResponseMessage signIn = await factory.CreateClient().PostAsJsonAsync("/api/auth/sign-in", new SignInRequest
        {
            Email = IntegrationTestAdmin.Email,
            Password = IntegrationTestAdmin.Password
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        SignInResponse? admin = await signIn.Content
            .ReadFromJsonAsync<SignInResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(admin?.AccessToken);
        return admin.AccessToken;
    }

    private static Task<HttpResponseMessage> ArchiveUnitAsync(HttpClient client, Guid unitId, string adminToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, $"/api/catalog/units/{unitId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private static Task<HttpResponseMessage> ArchivePropertyAsync(HttpClient client, Guid propertyId, string adminToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, $"/api/catalog/properties/{propertyId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        return client.SendAsync(request, TestContext.Current.CancellationToken);
    }

    private Task<HttpResponseMessage> CreateUnitAsync(Guid propertyId, string adminToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, "/api/catalog/units")
        {
            Content = JsonContent.Create(new CreateUnitRequest
            {
                PropertyId = propertyId,
                Name = new Dictionary<string, string> { { "en", "Late Room" } },
                MaxOccupancy = 2,
                BasePrice = 100m
            }, options: TestJsonOptions.Default)
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        return factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ACheckoutThatCompletesAndPaysWhileArchivalIsDeciding_BlocksTheArchive()
    {
        // The interleaving the advisory lock does not cover. It excludes *new*
        // holds; it does not freeze an existing one, because neither
        // ConfirmHoldAsync nor MarkHoldPaidAsync takes it. So a hold that was
        // already there when archival started can run all the way to 'booked'
        // while archival is between its two checks.
        //
        // That mattered because HasActiveHoldForUnitAsync deliberately excludes
        // 'booked'. With bookings checked first, a checkout completing in the
        // gap fell through both: no booking existed when the booking check ran,
        // and the hold was 'booked' - and therefore invisible - by the time the
        // hold check ran. The unit got archived with a live paid stay on it.
        Unit unit = CreateTestUnit();

        using (IServiceScope scope = factory.Services.CreateScope())
        {
            AppCatalogDbContext catalog = scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>();
            catalog.AddRange(_pendingProperties);
            catalog.Add(unit);
            await catalog.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        string adminToken = await SignInAsAdministratorAsync();

        // A live 'held' hold, in place before archival begins. This is what
        // makes the race reachable at all: the lock would refuse to let one
        // appear later.
        DateOnly checkIn = CatalogSeeding.Today().AddDays(80);
        HttpClient guest = factory.CreateClient();

        HttpResponseMessage holdResponse = await guest.PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, holdResponse.StatusCode);
        HoldAvailabilityResponse? hold = await holdResponse.Content
            .ReadFromJsonAsync<HoldAvailabilityResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(hold);

        FirstCheckGate gate = new FirstCheckGate(unit.Id);
        HttpClient archiveClient = ClientPausingAtTheFirstCheck(gate);

        // Act - archival is now parked with one of its two checks behind it,
        // holding the exclusive lock and a hold that is still merely 'held'.
        Task<HttpResponseMessage> archive = ArchiveUnitAsync(archiveClient, unit.Id, adminToken);
        await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // The whole checkout, start to finish, inside that window. Neither half
        // of it takes the advisory lock, which is exactly why it can run here.
        HttpResponseMessage confirmResponse = await guest.PostAsJsonAsync("/api/bookings", new ConfirmBookingRequest
        {
            HoldId = hold.HoldId,
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        }, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, confirmResponse.StatusCode);
        ConfirmBookingResponse? booking = await confirmResponse.Content
            .ReadFromJsonAsync<ConfirmBookingResponse>(TestJsonOptions.Default, TestContext.Current.CancellationToken);
        Assert.NotNull(booking);

        // Paying is what moves the hold to 'booked' and into the hold check's
        // blind spot. Without this the hold stays 'pending_payment', which the
        // hold check does see - a different and much easier case.
        using (IServiceScope paymentScope = factory.Services.CreateScope())
        {
            Assert.True(await paymentScope.ServiceProvider.GetRequiredService<IBookingPaymentConfirmation>()
                .ConfirmPaymentAsync(booking.BookingId, TestContext.Current.CancellationToken));
        }

        gate.Gate.SetResult();

        HttpResponseMessage response = await archive;

        // Assert - archival is refused. 409, the same answer a unit with an
        // ordinary live booking gets.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);

        using IServiceScope assertScope = factory.Services.CreateScope();

        Assert.True(
            await assertScope.ServiceProvider.GetRequiredService<AppCatalogDbContext>()
                .Units.AsNoTracking().AnyAsync(u => u.Id == unit.Id, TestContext.Current.CancellationToken),
            "The unit was archived with a paid, confirmed booking against it.");

        // And the stay survived intact - the thing that would actually have
        // been lost.
        AppBookingsDbContext bookings = assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>();

        Booking persisted = await bookings.Bookings.AsNoTracking()
            .SingleAsync(b => b.Id == booking.BookingId, TestContext.Current.CancellationToken);
        Assert.Equal(BookingStatus.Confirmed, persisted.BookingStatus);

        UnitAvailabilityHold soldHold = await bookings.UnitAvailabilityHolds.AsNoTracking()
            .SingleAsync(h => h.Id == hold.HoldId, TestContext.Current.CancellationToken);
        Assert.Equal("booked", soldHold.Status);
    }
}
