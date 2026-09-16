using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Persistence;
using Reviews.Entities;
namespace Reviews;

/// <summary>Reviews' view of <see cref="AppDbContext"/>; the module reaches the database only through it.</summary>
public sealed class ReviewsDb(AppDbContext db)
{
    public DbSet<StayReview> StayReviews => db.Set<StayReview>();
    public DbSet<GuestReview> GuestReviews => db.Set<GuestReview>();

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
