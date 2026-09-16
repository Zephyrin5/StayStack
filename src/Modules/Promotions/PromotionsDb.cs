using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Persistence;
using Promotions.Entities;
namespace Promotions;

/// <summary>Promotions' view of <see cref="AppDbContext"/>; the module reaches the database only through it.</summary>
public sealed class PromotionsDb(AppDbContext db)
{
    public DbSet<Promotion> Promotions => db.Set<Promotion>();

    // Written through Dapper inside the caller's transaction; mapped so migrations own its schema.
    public DbSet<PromotionRedemption> PromotionRedemptions => db.Set<PromotionRedemption>();

    public DatabaseFacade Database => db.Database;
    public ChangeTracker ChangeTracker => db.ChangeTracker;

    public EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class => db.Entry(entity);
    public EntityEntry<TEntity> Add<TEntity>(TEntity entity) where TEntity : class => db.Add(entity);
    public void AddRange(params object[] entities) => db.AddRange(entities);
    public void AddRange(IEnumerable<object> entities) => db.AddRange(entities);
    public EntityEntry<TEntity> Remove<TEntity>(TEntity entity) where TEntity : class => db.Remove(entity);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
