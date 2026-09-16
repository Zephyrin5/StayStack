using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Persistence;
using Catalog.Entities;
namespace Catalog;

/// <summary>Catalog's view of <see cref="AppDbContext"/>; the module reaches the database only through it.</summary>
public sealed class CatalogDb(AppDbContext db)
{
    public DbSet<Property> Properties => db.Set<Property>();
    public DbSet<Unit> Units => db.Set<Unit>();
    public DbSet<PricingRule> PricingRules => db.Set<PricingRule>();

    public DatabaseFacade Database => db.Database;
    public ChangeTracker ChangeTracker => db.ChangeTracker;

    public EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class => db.Entry(entity);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
