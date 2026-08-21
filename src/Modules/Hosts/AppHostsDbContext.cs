using Hosts.Entities;
using Hosts.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Hosts;

public class AppHostsDbContext(DbContextOptions<AppHostsDbContext> options) : StayStackDbContext(options)
{
    public DbSet<Host> Hosts => Set<Host>();

    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new HostConfiguration());
    }
}
