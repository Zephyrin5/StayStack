using Availability.Entities;
using Availability.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Availability;

public class AppAvailabilityDbContext(DbContextOptions<AppAvailabilityDbContext> options) : StayStackDbContext(options)
{
    // Mapped here so EF migrations own its schema (one migration history
    // per module, no drift between "what EF thinks exists" and what Dapper
    // actually queries) - but runtime reads/writes to this table go
    // through Dapper for its write paths, not this DbSet. See
    // UnitAvailabilityHold's own doc comment for why.
    public DbSet<UnitAvailabilityHold> UnitAvailabilityHolds => Set<UnitAvailabilityHold>();

    // Note: named OnStayStackModelCreating, not OnModelCreating - the base
    // class seals OnModelCreating so the soft-delete filter it applies
    // afterward can never be skipped. See StayStackDbContext.
    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new UnitAvailabilityHoldConfiguration());
    }
}
