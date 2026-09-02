using Availability;
using Bookings;
using Catalog;
using Hosts;
using Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Persistence;
using Promotions;
using Reviews;
using Testcontainers.PostgreSql;
using TickerQ.EntityFrameworkCore.DbContextFactory;
using Transactions;
namespace IntegrationTests;

// ReSharper disable once ClassNeverInstantiated.Global
public class IntegrationTestWebApplicationFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Pass the image directly into PostgreSqlBuilder constructor to fix CS0618
    private readonly PostgreSqlContainer _dbContainer = new PostgreSqlBuilder("postgres:16-alpine")
        .WithDatabase("staystack_test_db")
        .WithUsername("postgres")
        .WithPassword("postgres")
        .Build();

    public async ValueTask InitializeAsync()
    {
        await _dbContainer.StartAsync();
        await MigrateAllModulesAsync();
    }

    public override async ValueTask DisposeAsync()
    {
        // base first: it disposes the host, which closes its Npgsql data
        // sources and their pooled connections. Stopping the container out
        // from under a live pool just makes the shutdown noisier.
        //
        // This used to be `public new`, which hid WebApplicationFactory's own
        // DisposeAsync rather than extending it - so xUnit called this, the
        // container stopped, and the host was never disposed at all. Same
        // family of bug as the provider below: something built and never torn
        // down.
        await base.DisposeAsync();
        await _dbContainer.StopAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    ///     Applies every module's real migrations, before the host is built.
    ///     <para>
    ///         This used to call services.BuildServiceProvider() inside
    ///         ConfigureServices (the ASP0000 anti-pattern), which stood up a
    ///         SECOND container with its own copy of every singleton - a second
    ///         set of Npgsql data sources and connection pools included - and
    ///         then never disposed it. The `using` there was on the scope, not
    ///         the provider, so all of that leaked for the life of the test run.
    ///     </para>
    ///     <para>
    ///         The obvious fix is to migrate from this factory's own Services
    ///         instead, but that is wrong here: touching Services builds AND
    ///         starts the host, and this app registers TickerQ unconditionally
    ///         (Program.cs calls UseTickerQ()), so its scheduler would come up
    ///         against a database with no schema yet. Ordering is why the
    ///         original code ran inside ConfigureServices at all.
    ///     </para>
    ///     <para>
    ///         So: no container at all. Each context is constructed directly
    ///         from the same ConfigureStayStackDefaults the app registers it
    ///         with, migrated, and disposed. Nothing is left behind, and
    ///         migrations still complete before anything is hosted.
    ///     </para>
    /// </summary>
    private async Task MigrateAllModulesAsync()
    {
        // moduleName must match what each module's own registration passes -
        // it selects that module's migrations-history table, so a mismatch
        // would silently re-run every migration into the wrong bookkeeping.
        await MigrateAsync<AppIdentityDbContext>("identity");
        await MigrateAsync<AppCatalogDbContext>("catalog");
        await MigrateAsync<AppHostsDbContext>("hosts");
        await MigrateAsync<AppPromotionsDbContext>("promotions");
        await MigrateAsync<AppAvailabilityDbContext>("availability");
        await MigrateAsync<AppBookingsDbContext>("bookings");
        await MigrateAsync<AppTransactionsDbContext>("transactions");
        await MigrateAsync<AppReviewsDbContext>("reviews");

        // TickerQ's migrations live in the Jobs assembly, not alongside its
        // context - same reason TickerQDbContextDesignTimeFactory spells this
        // out for dotnet ef.
        await MigrateAsync<TickerQDbContext>("jobs", migrationsAssembly: "Jobs");
    }

    // Activator rather than a Func<DbContextOptions<TContext>, TContext>
    // parameter: passing the factory in would make every call site repeat its
    // own type name twice. Every context here is a plain EF context with the
    // standard (DbContextOptions<T>) constructor, and the whole suite fails
    // loudly on the first test if one ever isn't.
    private async Task MigrateAsync<TContext>(string moduleName, string? migrationsAssembly = null)
        where TContext : DbContext
    {
        DbContextOptionsBuilder<TContext> builder = new DbContextOptionsBuilder<TContext>();
        builder.ConfigureStayStackDefaults(
            _dbContainer.GetConnectionString(), moduleName, isDevelopment: false, migrationsAssembly);

        // All modules share one physical database, which Migrate() handles
        // safely regardless of call order - each tracks its own applied
        // migrations in its own history table, rather than checking whether
        // the database itself already exists the way EnsureCreated() does.
        await using TContext context = (TContext)Activator.CreateInstance(typeof(TContext), builder.Options)!;
        await context.Database.MigrateAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // TickerQDbContext can't go through the RemoveAll<DbContextOptions<...>>
        // + fresh AddDbContext override every other module's context uses
        // below - AddOperationalStore registers it through its own internal
        // wiring, so RemoveAll<DbContextOptions<TickerQDbContext>> has
        // nothing to remove (confirmed: resolving it after that override
        // still produced a context with no connection string). Feeding the
        // container's connection string through configuration instead
        // means JobsServicesRegistration's own GetConnectionString call -
        // the same one that runs in production - picks it up naturally.
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection([
                new KeyValuePair<string, string?>("ConnectionStrings:AppConnection", _dbContainer.GetConnectionString())
            ]);
        });

        builder.ConfigureServices(services =>
        {
            // Production registration always registers these DbContexts
            // now, regardless of environment - it has no "am I under test"
            // awareness to get wrong. Overriding them here is this test
            // host's job, using the RemoveAll<DbContextOptions<...>> +
            // fresh AddDbContext pattern ASP.NET Core's own docs recommend:
            // RemoveAll first, since a second AddDbContext alone wouldn't
            // replace the options the first one already registered.
            //
            // Reusing ConfigureStayStackDefaults, not a hand-rolled
            // UseNpgsql/UseSnakeCaseNamingConvention, keeps this test
            // config from drifting out of sync with production - a
            // hand-rolled version once missed the snake_case convention
            // the hand-written Dapper SQL and Postgres exclusion
            // constraints depend on.
            services.RemoveAll<DbContextOptions<AppIdentityDbContext>>();
            services.AddDbContext<AppIdentityDbContext>(options =>
                options.ConfigureStayStackDefaults(_dbContainer.GetConnectionString(), "identity", false));

            services.RemoveAll<DbContextOptions<AppCatalogDbContext>>();
            services.AddDbContext<AppCatalogDbContext>(options =>
                options.ConfigureStayStackDefaults(_dbContainer.GetConnectionString(), "catalog", false));

            services.RemoveAll<DbContextOptions<AppHostsDbContext>>();
            services.AddDbContext<AppHostsDbContext>(options =>
                options.ConfigureStayStackDefaults(_dbContainer.GetConnectionString(), "hosts", false));

            services.RemoveAll<DbContextOptions<AppPromotionsDbContext>>();
            services.AddDbContext<AppPromotionsDbContext>(options =>
                options.ConfigureStayStackDefaults(_dbContainer.GetConnectionString(), "promotions", false));

            services.RemoveAll<DbContextOptions<AppAvailabilityDbContext>>();
            services.AddDbContext<AppAvailabilityDbContext>(options =>
                options.ConfigureStayStackDefaults(_dbContainer.GetConnectionString(), "availability", false));

            services.RemoveAll<DbContextOptions<AppBookingsDbContext>>();
            services.AddDbContext<AppBookingsDbContext>(options =>
                options.ConfigureStayStackDefaults(_dbContainer.GetConnectionString(), "bookings", false));

            services.RemoveAll<DbContextOptions<AppTransactionsDbContext>>();
            services.AddDbContext<AppTransactionsDbContext>(options =>
                options.ConfigureStayStackDefaults(_dbContainer.GetConnectionString(), "transactions", false));

            services.RemoveAll<DbContextOptions<AppReviewsDbContext>>();
            services.AddDbContext<AppReviewsDbContext>(options =>
                options.ConfigureStayStackDefaults(_dbContainer.GetConnectionString(), "reviews", false));
        });
    }
}
