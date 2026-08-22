using Bookings;
using Catalog;
using Hosts;
using Identity;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace UnitTests.Persistence;

// HasPendingModelChanges() compares the live entity model against the last
// migration's frozen snapshot - the same check Database.Migrate() runs
// before applying anything, and the one PasswordHasher.HashPassword() and
// an unset ConcurrencyStamp both silently failed inside HasData() (see
// UserConfiguration.cs / RoleConfiguration.cs). Neither needs a real
// database: this is pure in-memory model comparison, so a connection
// string that's never actually dialed is fine here.
public class ModelHasNoPendingChangesTests
{
    private const string UnusedConnectionString = "Host=localhost;Database=staystack_probe;Username=postgres;Password=postgres;";

    [Fact]
    public void AppIdentityDbContext_HasNoPendingModelChanges()
    {
        DbContextOptionsBuilder<AppIdentityDbContext> builder = new DbContextOptionsBuilder<AppIdentityDbContext>();
        builder.ConfigureStayStackDefaults<AppIdentityDbContext>(UnusedConnectionString, "identity", isDevelopment: false);
        using AppIdentityDbContext context = new AppIdentityDbContext(builder.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void AppCatalogDbContext_HasNoPendingModelChanges()
    {
        DbContextOptionsBuilder<AppCatalogDbContext> builder = new DbContextOptionsBuilder<AppCatalogDbContext>();
        builder.ConfigureStayStackDefaults<AppCatalogDbContext>(UnusedConnectionString, "catalog", isDevelopment: false);
        using AppCatalogDbContext context = new AppCatalogDbContext(builder.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void AppHostsDbContext_HasNoPendingModelChanges()
    {
        DbContextOptionsBuilder<AppHostsDbContext> builder = new DbContextOptionsBuilder<AppHostsDbContext>();
        builder.ConfigureStayStackDefaults<AppHostsDbContext>(UnusedConnectionString, "hosts", isDevelopment: false);
        using AppHostsDbContext context = new AppHostsDbContext(builder.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void AppBookingsDbContext_HasNoPendingModelChanges()
    {
        DbContextOptionsBuilder<AppBookingsDbContext> builder = new DbContextOptionsBuilder<AppBookingsDbContext>();
        builder.ConfigureStayStackDefaults<AppBookingsDbContext>(UnusedConnectionString, "bookings", isDevelopment: false);
        using AppBookingsDbContext context = new AppBookingsDbContext(builder.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
