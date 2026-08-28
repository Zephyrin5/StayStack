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
    }

    public new async ValueTask DisposeAsync()
    {
        await _dbContainer.StopAsync();
        GC.SuppressFinalize(this);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // TickerQDbContext can't go through the RemoveAll<DbContextOptions<...>>
        // + fresh AddDbContext override every other module's context uses
        // below - AddOperationalStore registers it through its own internal
        // wiring rather than a plain AddDbContext<TickerQDbContext> call, so
        // RemoveAll<DbContextOptions<TickerQDbContext>> has nothing of
        // TickerQ's own to actually remove (confirmed: resolving
        // TickerQDbContext after that override still produced a context
        // with no connection string at all). Feeding the container's
        // connection string in through configuration instead means
        // JobsServicesRegistration's own configuration.GetConnectionString
        // call - the same one that runs in production - picks it up
        // naturally, with no need to fight TickerQ's registration mechanism.
        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection([
                new KeyValuePair<string, string?>("ConnectionStrings:AppConnection", _dbContainer.GetConnectionString())
            ]);
        });

        builder.ConfigureServices(services =>
        {
            // Production registration (ConfigureIdentityServices /
            // ConfigureCatalogServices / ConfigureHostsServices) always
            // registers these three DbContexts now, regardless of
            // environment - it has no "am I under test" awareness to get
            // wrong. Overriding them here is this test host's job, using
            // the same RemoveAll<DbContextOptions<...>> + fresh
            // AddDbContext pattern ASP.NET Core's own WebApplicationFactory
            // docs recommend: RemoveAll first because a second AddDbContext
            // call alone wouldn't replace the DbContextOptions<T> the first
            // one already registered.
            //
            // Reusing ConfigureStayStackDefaults (the same helper every
            // module's real registration goes through) instead of a
            // hand-rolled UseNpgsql/UseSnakeCaseNamingConvention here is
            // deliberate too: it's what keeps this test config from being
            // able to drift out of sync with production config, which is
            // exactly what happened the first time this was wired up by
            // hand (the hand-rolled version was missing the snake_case
            // convention the hand-written Dapper SQL and Postgres exclusion
            // constraints depend on).
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

            // Build a temporary provider just to apply each module's real
            // migrations before any test runs. All five share one physical
            // database in this container, which Migrate() handles safely
            // regardless of call order - each module tracks its own applied
            // migrations in its own history table (see
            // ConfigureStayStackDefaults), rather than checking whether the
            // database itself already exists the way EnsureCreated() does.
            ServiceProvider sp = services.BuildServiceProvider();
            using IServiceScope scope = sp.CreateScope();
            scope.ServiceProvider.GetRequiredService<AppIdentityDbContext>().Database.Migrate();
            scope.ServiceProvider.GetRequiredService<AppCatalogDbContext>().Database.Migrate();
            scope.ServiceProvider.GetRequiredService<AppHostsDbContext>().Database.Migrate();
            scope.ServiceProvider.GetRequiredService<AppPromotionsDbContext>().Database.Migrate();
            scope.ServiceProvider.GetRequiredService<AppAvailabilityDbContext>().Database.Migrate();
            scope.ServiceProvider.GetRequiredService<AppBookingsDbContext>().Database.Migrate();
            scope.ServiceProvider.GetRequiredService<AppTransactionsDbContext>().Database.Migrate();
            scope.ServiceProvider.GetRequiredService<AppReviewsDbContext>().Database.Migrate();
            scope.ServiceProvider.GetRequiredService<TickerQDbContext>().Database.Migrate();
        });
    }
}
