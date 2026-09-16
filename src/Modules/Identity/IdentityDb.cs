using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Persistence;
using Identity.Entities;
namespace Identity;

/// <summary>Identity's view of <see cref="AppDbContext"/>; the module reaches the database only through it.</summary>
public sealed class IdentityDb(AppDbContext db)
{
    public DbSet<RefreshToken> RefreshTokens => db.Set<RefreshToken>();

    public DatabaseFacade Database => db.Database;
    public ChangeTracker ChangeTracker => db.ChangeTracker;

    public EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class => db.Entry(entity);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
