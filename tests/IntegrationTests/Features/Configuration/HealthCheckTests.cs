using BuildingBlocks.Observability;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using System.Net;
namespace IntegrationTests.Features.Configuration;

// AddHealthChecks() with nothing registered reports Healthy unconditionally,
// so /health answered "yes" with the database unreachable - an orchestrator
// would keep routing to a node that could not serve a single request, and a
// deploy that could not reach its database would roll out green.
[Collection("Integration Tests")]
public class HealthCheckTests(IntegrationTestWebApplicationFactory factory)
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Readiness_ActuallyChecksSomething()
    {
        // The regression guard. A readiness endpoint that runs zero checks
        // passes every other test in this file too: it returns 200 because
        // nothing ran, not because anything was verified. Asserting the
        // registration exists, by name and tag, is what tells those two
        // apart - and the tag is what decides whether it runs at all, so a
        // check registered without it would be silently dead.
        HealthCheckServiceOptions options = factory.Services
            .GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;

        HealthCheckRegistration postgres = Assert.Single(
            options.Registrations, registration => registration.Name == "postgres");

        Assert.Contains(HealthCheckTags.Ready, postgres.Tags);
    }

    [Fact]
    public async Task BothProbes_PassWhenTheDatabaseIsReachable()
    {
        HttpResponseMessage live = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        HttpResponseMessage ready = await _client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    [Fact]
    public async Task AFailingDependency_TakesReadinessDownButNotLiveness()
    {
        // The whole point of the split, and the one behaviour worth proving:
        // an orchestrator restarts a container that fails liveness and merely
        // stops routing to one that fails readiness. If a dependency outage
        // read as "not alive", every node would fail at once and the
        // deployment would enter a restart loop - piling reconnecting nodes
        // onto an already-struggling database.
        //
        // A stub rather than a broken connection string: the test host runs
        // migrations at startup, so a host pointed at an unreachable database
        // would fail before it could answer a probe, and would be proving
        // something about startup rather than about the probes.
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHealthChecks()
                    .AddCheck("stub-dependency", () => HealthCheckResult.Unhealthy(), tags: [HealthCheckTags.Ready])));

        using HttpClient client = host.CreateClient();

        HttpResponseMessage live = await client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        HttpResponseMessage ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task ProbesSayNothingAboutWhatFailed()
    {
        // Health endpoints are anonymous by design, so the response must not
        // become a way to learn the database host, port or user - which is
        // exactly what an Npgsql failure message names.
        using WebApplicationFactory<Program> host = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddHealthChecks()
                    .AddCheck("stub-dependency",
                        () => HealthCheckResult.Unhealthy("Host=secret-db;Port=5432;Username=postgres"),
                        tags: [HealthCheckTags.Ready])));

        using HttpClient client = host.CreateClient();

        HttpResponseMessage ready = await client.GetAsync("/health/ready", TestContext.Current.CancellationToken);
        string body = await ready.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal("Unhealthy", body);
        Assert.DoesNotContain("secret-db", body, StringComparison.OrdinalIgnoreCase);
    }
}
