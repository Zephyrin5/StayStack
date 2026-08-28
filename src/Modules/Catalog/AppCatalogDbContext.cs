using Catalog.Entities;
using Catalog.Entities.Configurations;
using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Catalog;

public class AppCatalogDbContext(DbContextOptions<AppCatalogDbContext> options) : StayStackDbContext(options)
{
    public DbSet<Property> Properties => Set<Property>();
    public DbSet<Unit> Units => Set<Unit>();

    // Unlike UnitAvailabilityHolds (moved to the Availability module - see
    // docs/adr/0004), reads/writes to this table go through
    // EF change tracking normally - rule-authoring is a low-frequency
    // admin action, not a hot concurrent-write path. See docs/adr/0012.
    public DbSet<PricingRule> PricingRules => Set<PricingRule>();

    // Note: named OnStayStackModelCreating, not OnModelCreating - the base
    // class seals OnModelCreating so the soft-delete filter it applies
    // afterward can never be skipped. See StayStackDbContext.
    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        // Backs PropertyConfiguration's GIN trigram index on City - a plain
        // B-tree index can't serve GetPropertiesHandler's ILIKE '%term%'
        // (leading wildcard), so the search stays a full scan without this.
        modelBuilder.HasPostgresExtension("pg_trgm");

        modelBuilder.ApplyConfiguration(new PropertyConfiguration());
        modelBuilder.ApplyConfiguration(new UnitConfiguration());
        modelBuilder.ApplyConfiguration(new PricingRuleConfiguration());
    }
}
