using Hosts.Entities;
using Hosts.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Hosts;

// Kept only so this module's existing migrations compile; nothing registers or uses it. The
// model lives in the module's IModuleModel, and this class goes when the migrations are squashed.
public class AppHostsDbContext(DbContextOptions<AppHostsDbContext> options) : StayStackDbContext(options)
{
    public DbSet<Host> Hosts => Set<Host>();

    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new HostConfiguration());
    }
}
