using Catalog;
using Hosts;
using Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Persistence;
using Testcontainers.PostgreSql;
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
                options.ConfigureStayStackDefaults<AppIdentityDbContext>(_dbContainer.GetConnectionString(), "identity", isDevelopment: false));

            services.RemoveAll<DbContextOptions<AppCatalogDbContext>>();
            services.AddDbContext<AppCatalogDbContext>(options =>
                options.ConfigureStayStackDefaults<AppCatalogDbContext>(_dbContainer.GetConnectionString(), "catalog", isDevelopment: false));

            services.RemoveAll<DbContextOptions<AppHostsDbContext>>();
            services.AddDbContext<AppHostsDbContext>(options =>
                options.ConfigureStayStackDefaults<AppHostsDbContext>(_dbContainer.GetConnectionString(), "hosts", isDevelopment: false));

            // Build a temporary provider just to apply each module's real
            // migrations before any test runs. All three share one physical
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
        });
    }
}
