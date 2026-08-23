using Api.RateLimiting;
using Identity.Features.SignIn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
namespace IntegrationTests.Features.Auth;

// appsettings.Testing.json deliberately sets a very high AuthPermitLimit so
// the shared IntegrationTestWebApplicationFactory (one instance, one rate
// limiter, reused by every other test in the collection) never trips it on
// ordinary test traffic. This test overrides the limit back down on its
// own WithWebHostBuilder-derived client (same underlying Postgres
// container, just extra DI configuration layered on top) specifically to
// prove RequireRateLimiting("auth") actually rejects with a 429 once the
// limit is exceeded.
[Collection("Integration Tests")]
public class RateLimitingTests(IntegrationTestWebApplicationFactory factory)
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
                    o.AuthPermitLimit = limit;
                    o.AuthWindowSeconds = 60;
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
}
