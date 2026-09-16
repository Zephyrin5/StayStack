using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Persistence;
namespace Database;

/// <summary>
///     Builds <see cref="AppDbContext"/> for <c>dotnet ef</c>, which needs a model but never a working
///     connection: migrations are generated from the model, and applied by the app.
/// </summary>
public sealed class DesignTimeAppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<AppDbContext> options = new DbContextOptionsBuilder<AppDbContext>();

        options.ConfigureStayStackDefaults(
            Environment.GetEnvironmentVariable("STAYSTACK_DESIGN_TIME_CONNECTION")
            ?? "Host=localhost;Database=staystack;Username=postgres;Password=postgres",
            "app",
            isDevelopment: false,
            migrationsAssembly: "Database");

        return new AppDbContext(options.Options, AppDbContextModels.All);
    }
}
