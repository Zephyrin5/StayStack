using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.Configuration;
using Persistence;
using TickerQ.EntityFrameworkCore.DbContextFactory;
namespace Jobs;

/// <summary>
///     dotnet ef's DI-based discovery (the same path that finds every other
///     module's DbContext through Program.cs's registered services) never
///     finds TickerQDbContext - AddOperationalStore registers it through
///     TickerQ's own internal wiring rather than a plain
///     services.AddDbContext&lt;TickerQDbContext&gt; call the tooling can
///     reflect over (confirmed: `dotnet ef dbcontext list` enumerates every
///     other module's context but omits this one entirely - a known,
///     currently-unresolved gap upstream, not a mistake in this project's
///     own registration). An IDesignTimeDbContextFactory bypasses that
///     discovery path entirely: dotnet ef uses this directly instead.
/// </summary>
public sealed class TickerQDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TickerQDbContext>
{
    // Api.csproj's own UserSecretsId - read directly by GUID (not
    // AddUserSecrets&lt;Program&gt;()) since this factory lives in Jobs'
    // assembly, which carries no [UserSecretsId] attribute of its own.
    // User secrets are keyed by this id alone, not by working directory, so
    // this resolves the real local connection string regardless of where
    // dotnet ef is invoked from - needed for `database update` to actually
    // connect, not just `migrations add`, which only builds a model and
    // never opens a connection at all.
    private const string ApiUserSecretsId = "72863d56-ceae-4ea2-8a19-3f5f87fbe979";

    public TickerQDbContext CreateDbContext(string[] args)
    {
        IConfigurationRoot configuration = new ConfigurationBuilder()
            .AddUserSecrets(ApiUserSecretsId)
            .Build();

        string connectionString = configuration.GetConnectionString("AppConnection")
                                  ?? "Host=localhost;Database=staystack;Username=postgres;Password=postgres";

        DbContextOptionsBuilder<TickerQDbContext> optionsBuilder = new DbContextOptionsBuilder<TickerQDbContext>();
        optionsBuilder.ConfigureStayStackDefaults(
            connectionString,
            "jobs",
            isDevelopment: false,
            migrationsAssembly: "Jobs");

        return new TickerQDbContext(optionsBuilder.Options);
    }
}
