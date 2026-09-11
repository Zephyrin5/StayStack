using Bookings;
using Bookings.Features.HoldAvailability;
using Catalog;
using Catalog.Entities;
using Identity.Features.SignIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SeedWork.ValueObjects;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Catalog;

// Archiving a unit checked "no active bookings or holds" and then archived,
// with nothing spanning the two. A hold taken in that gap left a unit archived
// with live inventory against it. Existing tests only cover "a booking already
// exists", which the check catches on its own.
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

        // Authenticated up front, deliberately. Signing in inside the archive
        // task would delay it by a whole round trip, so the two requests would
        // not overlap and the test would pass without ever exercising the
        // interleaving it exists for.
        string adminToken = await SignInAsAdministratorAsync();

        DateOnly checkIn = CatalogSeeding.Today().AddDays(60);

        // The interleaving, driven from both sides at once. Whichever order
        // they land in, the outcome has to be consistent: an archived unit
        // must not have a live hold, and a live hold must not be against an
        // archived unit. Before the lock, both could win.
        Task<HttpResponseMessage> hold = factory.CreateClient().PostAsJsonAsync("/api/availability/holds",
            new HoldAvailabilityRequest
            {
                UnitId = unit.Id,
                CheckIn = checkIn,
                CheckOut = checkIn.AddDays(2),
                GuestCount = 2
            }, TestContext.Current.CancellationToken);

        Task<HttpResponseMessage> archive = ArchiveAsync(unit.Id, adminToken);

        HttpResponseMessage[] responses = await Task.WhenAll(hold, archive);

        bool holdSucceeded = responses[0].StatusCode == HttpStatusCode.OK;
        bool archiveSucceeded = responses[1].StatusCode == HttpStatusCode.OK;

        using IServiceScope assertScope = factory.Services.CreateScope();

        bool unitIsArchived = !await assertScope.ServiceProvider.GetRequiredService<AppCatalogDbContext>()
            .Units.AsNoTracking()
            .AnyAsync(u => u.Id == unit.Id, TestContext.Current.CancellationToken);

        bool holdExists = await assertScope.ServiceProvider.GetRequiredService<AppBookingsDbContext>()
            .UnitAvailabilityHolds.AsNoTracking()
            .AnyAsync(h => h.UnitId == unit.Id && h.Status == "held", TestContext.Current.CancellationToken);

        Assert.False(
            unitIsArchived && holdExists,
            $"The unit was archived with a live hold against it (hold {holdSucceeded}, archive {archiveSucceeded}). " +
            "The guard and the archive have to be one decision, not two.");

        // And the run is only meaningful if both sides actually did something
        // - two failures would satisfy the assertion above while testing
        // nothing at all.
        Assert.True(holdSucceeded || archiveSucceeded);
    }

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

    private Task<HttpResponseMessage> ArchiveAsync(Guid unitId, string adminToken)
    {
        HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Delete, $"/api/catalog/units/{unitId}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        return factory.CreateClient().SendAsync(request, TestContext.Current.CancellationToken);
    }
}
