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

    // CRUD goes through EF change tracking normally, same reasoning as
    // PricingRules above - RedemptionCount's own hot-path increment is the
    // one exception, done via raw SQL. See Promotion's own doc comment.
    public DbSet<Promotion> Promotions => Set<Promotion>();

    // Mapped here so EF migrations own its schema, but written through
    // Dapper inside the same transaction as Promotion's redemption-count
    // increment, not DbContext.SaveChanges() - same split as
    // UnitAvailabilityHolds above. See PromotionRedemption's own doc
    // comment.
    public DbSet<PromotionRedemption> PromotionRedemptions => Set<PromotionRedemption>();

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
        modelBuilder.ApplyConfiguration(new UnitAvailabilityHoldConfiguration());
        modelBuilder.ApplyConfiguration(new PricingRuleConfiguration());
        modelBuilder.ApplyConfiguration(new PromotionConfiguration());
        modelBuilder.ApplyConfiguration(new PromotionRedemptionConfiguration());
    }
}
