using Api.RateLimiting;
using Bookings.Features.ConfirmBooking;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Auth;

// appsettings.Testing.json deliberately sets a very high limit for each
// policy so
// the shared IntegrationTestWebApplicationFactory (one instance, one rate
// limiter, reused by every other test in the collection) never trips it on
// ordinary test traffic. This test overrides the limit back down on its
// own WithWebHostBuilder-derived client (same underlying Postgres
// container, just extra DI configuration layered on top) specifically to
// prove RequireRateLimiting("auth") actually rejects with a 429 once the
// limit is exceeded.
[Collection(AuthCollection.Name)]
public class RateLimitingTests(AuthFixture factory)
{
    [Fact]
    public async Task SignIn_ShouldReturn429_AfterExceedingConfiguredLimit()
    {
        const int limit = 3;

        HttpClient client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<AuthRateLimitOptions>(o =>
                {
                    o.PermitLimit = limit;
                    o.WindowSeconds = 60;
                });
            });
        }).CreateClient();

        SignInRequest request = new SignInRequest { Email = "nobody@example.com", Password = "wrong-password" };

        HttpResponseMessage? lastResponse = null;
        for (int i = 0; i < limit + 1; i++)
        {
            lastResponse = await client.PostAsJsonAsync("/api/auth/sign-in", request, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(lastResponse);
        Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
    }

    [Fact]
    public async Task ConfirmBooking_ShouldReturn429_AfterExceedingConfiguredLimit()
    {
        // ConfirmBookingEndpoint attaches the same "auth" policy as
        // SignIn/CancelBooking (docs/adr/0016). The limiter runs before the
        // request reaches the handler, so a bogus HoldId still proves the
        // wiring - the 429 has to come from the policy.
        const int limit = 3;

        HttpClient client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<AuthRateLimitOptions>(o =>
                {
                    o.PermitLimit = limit;
                    o.WindowSeconds = 60;
                });
            });
        }).CreateClient();

        ConfirmBookingRequest request = new ConfirmBookingRequest
        {
            HoldId = Guid.NewGuid(),
            GuestName = "Jane Guest",
            GuestEmail = "jane@example.com"
        };

        HttpResponseMessage? lastResponse = null;
        for (int i = 0; i < limit + 1; i++)
        {
            lastResponse = await client.PostAsJsonAsync("/api/bookings", request, TestContext.Current.CancellationToken);
        }

        Assert.NotNull(lastResponse);
        Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
    }

    [Fact]
    public async Task AnonymousReads_ShouldReturn429_AfterExceedingConfiguredLimit()
    {
        // GetProperties, GetPropertyById, GetPriceCalendar and
        // GetPropertyReviews were unauthenticated with no limiter at all.
        // Their per-request cost is bounded - the stay-window caps, the price
        // calendar's date bounds, MaxOffset, the HybridCache limits - but
        // nothing bounded how many a caller could issue, and each cache miss
        // still costs a cross-module availability call and a pricing load.
        //
        // Same shape as the auth test above: the shared factory runs with a
        // very high reads limit so ordinary test traffic never trips it,
        // and this client overrides it back down to prove the policy is
        // actually attached rather than merely defined.
        const int limit = 3;

        HttpClient client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<ReadRateLimitOptions>(o =>
                {
                    o.PermitLimit = limit;
                    o.WindowSeconds = 60;
                });
            });
        }).CreateClient();

        HttpResponseMessage? lastResponse = null;
        for (int i = 0; i < limit + 1; i++)
        {
            lastResponse = await client.GetAsync("/api/catalog/properties", TestContext.Current.CancellationToken);
        }

        Assert.NotNull(lastResponse);
        Assert.Equal(HttpStatusCode.TooManyRequests, lastResponse.StatusCode);
    }

    [Fact]
    public async Task TheReadLimit_IsSharedAcrossTheAnonymousReadEndpoints_NotPerEndpoint()
    {
        // One policy, one partition per caller IP, so the budget covers the
        // read surface as a whole. A per-endpoint budget would let a caller
        // multiply their allowance by rotating between four endpoints that
        // cost the same to serve.
        const int limit = 3;

        HttpClient client = factory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.Configure<ReadRateLimitOptions>(o =>
                {
                    o.PermitLimit = limit;
                    o.WindowSeconds = 60;
                });
            });
        }).CreateClient();

        // Spread across two different read endpoints, exceeding the shared
        // limit only in aggregate.
        await client.GetAsync("/api/catalog/properties", TestContext.Current.CancellationToken);
        await client.GetAsync($"/api/catalog/properties/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
        await client.GetAsync("/api/catalog/properties", TestContext.Current.CancellationToken);

        HttpResponseMessage response = await client.GetAsync(
            $"/api/catalog/properties/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
    }

}
