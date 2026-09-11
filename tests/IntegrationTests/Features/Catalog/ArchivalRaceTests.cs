using Bookings;
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
    // load-bearing rather than arbitrary. UnitArchival asks the booking guard
    // first and the hold lookup second, so pausing on the booking guard parks
    // the archive *between* its two checks - and on release the hold check runs
    // and sees the hold that just arrived. The archive then correctly refuses,
    // with or without a lock, and the test passes against the broken code. It
    // did exactly that on the first attempt.
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
}
