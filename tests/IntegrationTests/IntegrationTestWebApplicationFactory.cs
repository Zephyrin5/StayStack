using Database;
using IntegrationTests.Measurements;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Persistence;
using Persistence.Interceptors;
namespace IntegrationTests;

/// <summary>
///     A host on a database of this collection's own, copied from the assembly's migrated template
///     (see <see cref="PostgresFixture" />). One per collection, so collections can run at the same
///     time without reading each other's rows.
/// </summary>
public abstract class IntegrationTestWebApplicationFactory(PostgresFixture postgres, string collection)
    : WebApplicationFactory<Program>, IAsyncLifetime
{
    private IdleInTransactionProbe? _idleInTransactionProbe;

    private string? _connectionString;

    /// <summary>
    ///     Whether this host schedules TickerQ's jobs. Off by default: a job commits on its own cron,
    ///     and an injected commit fault cannot tell that commit from the request under test - the
    ///     request then runs clean while the fault reports it fired (docs/adr/0025). A host that needs
    ///     the scheduler says so.
    /// </summary>
    protected virtual bool RunsScheduledJobs => false;

    public string ConnectionString =>
        _connectionString ?? throw new InvalidOperationException("InitializeAsync has not run yet.");

    public async ValueTask InitializeAsync()
    {
        _connectionString = await postgres.CreateDatabaseAsync($"staystack_{collection}");

        // Stage 0 measurement only; off unless an output file is named.
        if (Environment.GetEnvironmentVariable("STAYSTACK_IDLE_TX_PROBE") is { Length: > 0 } probeOutput)
        {
            _idleInTransactionProbe = new IdleInTransactionProbe(_connectionString, probeOutput);
            _idleInTransactionProbe.Start();
        }

        // The administrator these tests sign in as, created here so the credential lives for one run
        // in one throwaway database. See IntegrationTestAdmin.
        await IntegrationTestAdmin.EnsureCreatedAsync(Services);
    }

    public override async ValueTask DisposeAsync()
    {
        // base first: it disposes the host, which closes its Npgsql data sources and their pooled
        // connections. The container outlives every collection, so nothing here stops it.
        //
        // An override, not `public new`: hiding WebApplicationFactory's own DisposeAsync would leave
        // the host undisposed.
        await base.DisposeAsync();

        if (_idleInTransactionProbe is not null)
        {
            await _idleInTransactionProbe.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    // With a stated pool size, because AddAppDbContext refuses to start without one outside
    // Development and these tests run the production registration path. Collections now run at the
    // same time against one server, so this is per host rather than per suite; 25 is still far above
    // what a collection uses, and the pool-exhaustion measurements set their own.
    private string TestConnectionString => $"{ConnectionString};Maximum Pool Size=25";

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting, not only ConfigureAppConfiguration: host configuration is what Program reads
        // when it registers services, and a layered configuration source arrives after that.
        builder.UseSetting("ConnectionStrings:AppConnection", TestConnectionString);
        builder.UseSetting("App:Jobs:RunScheduler", RunsScheduledJobs ? "true" : "false");

        // TickerQDbContext can't go through the RemoveAll<DbContextOptions<...>> + fresh
        // AddDbContext override below - AddOperationalStore registers it through its own internal
        // wiring, so RemoveAll<DbContextOptions<TickerQDbContext>> has nothing to remove (confirmed:
        // resolving it after that override still produced a context with no connection string).
        // Feeding the connection string through configuration instead means
        // JobsServicesRegistration's own GetConnectionString call - the same one that runs in
        // production - picks it up naturally.
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection([
                new KeyValuePair<string, string?>("ConnectionStrings:AppConnection", TestConnectionString)
            ]);
        });

        builder.ConfigureServices(services =>
        {
            // Production registration always registers these DbContexts now, regardless of
            // environment - it has no "am I under test" awareness to get wrong. Overriding them here
            // is this test host's job, using the RemoveAll<DbContextOptions<...>> + fresh
            // AddDbContext pattern ASP.NET Core's own docs recommend: RemoveAll first, since a second
            // AddDbContext alone wouldn't replace the options the first one already registered.
            //
            // Reusing ConfigureStayStackDefaults, not a hand-rolled UseNpgsql/
            // UseSnakeCaseNamingConvention, keeps this test config from drifting out of sync with
            // production - a hand-rolled version once missed the snake_case convention the
            // hand-written Dapper SQL and Postgres exclusion constraints depend on.
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>((serviceProvider, options) =>
            {
                options.ConfigureStayStackDefaults(ConnectionString, "app", false, migrationsAssembly: "Database");
                options.AddInterceptors(serviceProvider.GetRequiredService<AuditableEntitySaveChangesInterceptor>());
            });
        });
    }
}
