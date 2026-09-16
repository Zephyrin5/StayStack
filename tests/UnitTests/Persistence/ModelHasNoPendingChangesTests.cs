using Database;
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
    public void AppDbContext_HasNoPendingModelChanges()
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        builder.ConfigureStayStackDefaults(UnusedConnectionString, "app", false, migrationsAssembly: "Database");
        using AppDbContext context = new AppDbContext(builder.Options, AppDbContextModels.All);

        Assert.False(context.Database.HasPendingModelChanges());
    }
}
