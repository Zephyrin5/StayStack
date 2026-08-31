using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Persistence;
using TickerQ.EntityFrameworkCore.DbContextFactory;
namespace Jobs;

/// <summary>
///     dotnet ef's DI-based discovery never finds TickerQDbContext -
///     AddOperationalStore registers it through TickerQ's own internal
///     wiring, not a plain AddDbContext&lt;TickerQDbContext&gt; call the
///     tooling can reflect over (confirmed: `dotnet ef dbcontext list`
///     omits it entirely - an upstream gap, not a mistake here). This
///     factory bypasses that discovery path: dotnet ef uses it directly.
/// </summary>
public sealed class TickerQDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TickerQDbContext>
{
    // Api.csproj's own UserSecretsId, read directly by GUID (not
    // AddUserSecrets&lt;Program&gt;()) since this factory lives in Jobs'
    // assembly, which has no [UserSecretsId] of its own. Secrets are keyed
    // by this id, not working directory - needed for `database update` to
    // actually connect (`migrations add` only builds a model, no connection).
    private const string ApiUserSecretsId = "72863d56-ceae-4ea2-8a19-3f5f87fbe979";

    public TickerQDbContext CreateDbContext(string[] args)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddUserSecrets(ApiUserSecretsId)
            .Build();

        string connectionString = configuration.GetConnectionString("AppConnection")
                                  ?? throw new InvalidOperationException(
                                      $"Connection string 'AppConnection' not found in user secrets ({ApiUserSecretsId}).");

        DbContextOptionsBuilder<TickerQDbContext> optionsBuilder = new DbContextOptionsBuilder<TickerQDbContext>();
        optionsBuilder.ConfigureStayStackDefaults(
            connectionString,
            "jobs",
            isDevelopment: false,
            migrationsAssembly: "Jobs");

        return new TickerQDbContext(optionsBuilder.Options);
    }
}
