using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Persistence;
namespace UnitTests.Infrastructure;

// The integration tests prove the wiring - that a failing "ready" check takes
// readiness down and leaves liveness alone - using a stub. These prove the
// real check, which is the part that would quietly always return Healthy and
// pass every one of those tests anyway.
public class PostgresHealthCheckTests
{
    private static PostgresHealthCheck CreateCheck(string? connectionString)
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("ConnectionStrings:AppConnection", connectionString)])
            .Build();

        return new PostgresHealthCheck(configuration);
    }

    private static readonly HealthCheckContext Context = new HealthCheckContext
    {
        Registration = new HealthCheckRegistration("postgres", _ => null!, HealthStatus.Unhealthy, tags: null)
    };

    [Fact]
    public async Task AnUnreachableDatabase_ReportsUnhealthy()
    {
        // Port 1 on the loopback: nothing listens there, so this fails to
        // connect rather than hanging on a route to a real host.
        PostgresHealthCheck check = CreateCheck(
            "Host=127.0.0.1;Port=1;Database=staystack;Username=postgres;Password=postgres;Timeout=2");

        HealthCheckResult result = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task AnUnreachableDatabase_FailsWithinTheProbeBudget()
    {
        // Npgsql's own connect timeout defaults to 15 seconds, which outlasts
        // most orchestrator probe deadlines - the probe would be recorded as a
        // timeout rather than a failure, and each poll would leave another
        // connection attempt in flight behind it. The check bounds itself.
        PostgresHealthCheck check = CreateCheck(
            "Host=127.0.0.1;Port=1;Database=staystack;Username=postgres;Password=postgres");

        DateTimeOffset started = DateTimeOffset.UtcNow;
        HealthCheckResult result = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);
        TimeSpan elapsed = DateTimeOffset.UtcNow - started;

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
        Assert.True(elapsed < TimeSpan.FromSeconds(10), $"Took {elapsed.TotalSeconds:F1}s; the check should bound itself.");
    }

    [Fact]
    public async Task AMissingConnectionString_ReportsUnhealthy()
    {
        // Not an exception. A misconfigured node is exactly the node that
        // should be taken out of rotation, and a throw here would surface as
        // an unhandled failure rather than as a readiness answer.
        PostgresHealthCheck check = CreateCheck(null);

        HealthCheckResult result = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);

        Assert.Equal(HealthStatus.Unhealthy, result.Status);
    }

    [Fact]
    public async Task AFailure_NamesNoConnectionDetail()
    {
        // Health endpoints are anonymous by design, so nothing in the result
        // should be able to become a way to learn the database host or user
        // if a future response writer starts emitting descriptions.
        PostgresHealthCheck check = CreateCheck(
            "Host=secret-db-host;Port=1;Database=staystack;Username=secret-user;Password=p;Timeout=2");

        HealthCheckResult result = await check.CheckHealthAsync(Context, TestContext.Current.CancellationToken);

        Assert.Null(result.Exception);
        Assert.DoesNotContain("secret-db-host", result.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("secret-user", result.Description ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
