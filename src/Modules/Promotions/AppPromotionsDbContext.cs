using Microsoft.EntityFrameworkCore;
using Persistence;
using Promotions.Entities;
using Promotions.Entities.Configurations;
namespace Promotions;

// Kept only so this module's existing migrations compile; nothing registers or uses it. The
// model lives in the module's IModuleModel, and this class goes when the migrations are squashed.
public class AppPromotionsDbContext(DbContextOptions<AppPromotionsDbContext> options) : StayStackDbContext(options)
{
    // CRUD goes through EF change tracking normally - RedemptionCount's own
    // hot-path increment is the one exception, done via raw SQL. See
    // Promotion's own doc comment.
    public DbSet<Promotion> Promotions => Set<Promotion>();

    // Mapped here so EF migrations own its schema, but written through
    // Dapper inside the same transaction as Promotion's redemption-count
    // increment, not DbContext.SaveChanges() - see PromotionRedemption's
    // own doc comment.
    public DbSet<PromotionRedemption> PromotionRedemptions => Set<PromotionRedemption>();

    // Note: named OnStayStackModelCreating, not OnModelCreating - the base
    // class seals OnModelCreating so the soft-delete filter it applies
    // afterward can never be skipped. See StayStackDbContext.
    protected override void OnStayStackModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfiguration(new PromotionConfiguration());
        modelBuilder.ApplyConfiguration(new PromotionRedemptionConfiguration());
    }
}
