using Bookings;
using Catalog;
using Hosts;
using Identity;
using Promotions;
using Reviews;
using Transactions;
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
        var builder = new DbContextOptionsBuilder<AppIdentityDbContext>();
        builder.ConfigureStayStackDefaults(UnusedConnectionString, "identity", false);
        using AppIdentityDbContext context = new AppIdentityDbContext(builder.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void AppCatalogDbContext_HasNoPendingModelChanges()
    {
        var builder = new DbContextOptionsBuilder<AppCatalogDbContext>();
        builder.ConfigureStayStackDefaults(UnusedConnectionString, "catalog", false);
        using AppCatalogDbContext context = new AppCatalogDbContext(builder.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void AppHostsDbContext_HasNoPendingModelChanges()
    {
        var builder = new DbContextOptionsBuilder<AppHostsDbContext>();
        builder.ConfigureStayStackDefaults(UnusedConnectionString, "hosts", false);
        using AppHostsDbContext context = new AppHostsDbContext(builder.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void AppBookingsDbContext_HasNoPendingModelChanges()
    {
        var builder = new DbContextOptionsBuilder<AppBookingsDbContext>();
        builder.ConfigureStayStackDefaults(UnusedConnectionString, "bookings", false);
        using AppBookingsDbContext context = new AppBookingsDbContext(builder.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    // The three that had migration histories and no test. Nothing about them
    // made the check less applicable - they were simply added later than the
    // four above, which is exactly how this kind of coverage goes missing.
    [Fact]
    public void AppPromotionsDbContext_HasNoPendingModelChanges()
    {
        var builder = new DbContextOptionsBuilder<AppPromotionsDbContext>();
        builder.ConfigureStayStackDefaults(UnusedConnectionString, "promotions", false);
        using AppPromotionsDbContext context = new AppPromotionsDbContext(builder.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void AppTransactionsDbContext_HasNoPendingModelChanges()
    {
        var builder = new DbContextOptionsBuilder<AppTransactionsDbContext>();
        builder.ConfigureStayStackDefaults(UnusedConnectionString, "transactions", false);
        using AppTransactionsDbContext context = new AppTransactionsDbContext(builder.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public void AppReviewsDbContext_HasNoPendingModelChanges()
    {
        var builder = new DbContextOptionsBuilder<AppReviewsDbContext>();
        builder.ConfigureStayStackDefaults(UnusedConnectionString, "reviews", false);
        using AppReviewsDbContext context = new AppReviewsDbContext(builder.Options);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
