using Catalog.Entities;
using Catalog.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Catalog;

public class AppCatalogDbContext(DbContextOptions<AppCatalogDbContext> options) : StayStackDbContext(options)
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Unit> Units => Set<Unit>();

    // Mapped here so EF migrations own its schema (one migration history
    // per module, no drift between "what EF thinks exists" and what Dapper
    // actually queries) - but runtime reads/writes to this table go
    // through Dapper, not this DbSet. See UnitAvailabilityHold's own doc
    // comment for why.
    public DbSet<UnitAvailabilityHold> UnitAvailabilityHolds => Set<UnitAvailabilityHold>();

    // Unlike UnitAvailabilityHolds, reads/writes to this table go through
    // EF change tracking normally - rule-authoring is a low-frequency
    // admin action, not a hot concurrent-write path. See docs/adr/0012.
    public DbSet<PricingRule> PricingRules => Set<PricingRule>();

    // Note: named OnStayStackModelCreating, not OnModelCreating - the base
    // class seals OnModelCreating so the soft-delete filter it applies
    // afterward can never be skipped. See StayStackDbContext.
    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new PropertyConfiguration());
        modelBuilder.ApplyConfiguration(new UnitConfiguration());
        modelBuilder.ApplyConfiguration(new UnitAvailabilityHoldConfiguration());
        modelBuilder.ApplyConfiguration(new PricingRuleConfiguration());
    }
}
