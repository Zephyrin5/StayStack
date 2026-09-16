using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Persistence;
using Bookings.Entities;
namespace Bookings;

/// <summary>Bookings' view of <see cref="AppDbContext"/>; the module reaches the database only through it.</summary>
public sealed class BookingsDb(AppDbContext db)
{
    public DbSet<Booking> Bookings => db.Set<Booking>();
    public DbSet<BookingManagementToken> BookingManagementTokens => db.Set<BookingManagementToken>();
    public DbSet<CheckoutIdempotencyRecord> CheckoutIdempotencyRecords => db.Set<CheckoutIdempotencyRecord>();
    public DbSet<RefundObligation> RefundObligations => db.Set<RefundObligation>();
    public DbSet<UnitAvailabilityHold> UnitAvailabilityHolds => db.Set<UnitAvailabilityHold>();

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
