using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
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
///     discovery path entirely: dotnet ef uses this directly instead, so
///     the connection string here only needs to be well-formed enough for
///     Npgsql to build a model, not to actually connect - migrations add
///     doesn't open a connection.
/// </summary>
public sealed class TickerQDbContextDesignTimeFactory : IDesignTimeDbContextFactory<TickerQDbContext>
{
    public TickerQDbContext CreateDbContext(string[] args)
    {
        DbContextOptionsBuilder<TickerQDbContext> optionsBuilder = new DbContextOptionsBuilder<TickerQDbContext>();
        optionsBuilder.ConfigureStayStackDefaults(
            "Host=localhost;Database=staystack;Username=postgres;Password=postgres",
            "jobs",
            isDevelopment: false,
            migrationsAssembly: "Jobs");

        return new TickerQDbContext(optionsBuilder.Options);
    }
}
