using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Persistence;
using Transactions.Entities;
namespace Transactions;

/// <summary>Transactions' view of <see cref="AppDbContext"/>; the module reaches the database only through it.</summary>
public sealed class TransactionsDb(AppDbContext db)
{
    public DbSet<Transaction> Transactions => db.Set<Transaction>();

    public DatabaseFacade Database => db.Database;
    public ChangeTracker ChangeTracker => db.ChangeTracker;

    public EntityEntry<TEntity> Entry<TEntity>(TEntity entity) where TEntity : class => db.Entry(entity);

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        db.SaveChangesAsync(cancellationToken);
}
