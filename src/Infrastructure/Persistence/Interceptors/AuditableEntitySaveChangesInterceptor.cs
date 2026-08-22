using BuildingBlocks.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using SeedWork.Abstractions;
namespace Persistence.Interceptors;

/// <summary>
///     Registered once per module's DbContext (see
///     NpgsqlDbContextOptionsExtensions.ConfigureStayStackDefaults). Written
///     once here instead of being reimplemented inside every module's
///     OnModelCreating/SaveChanges.
/// </summary>
public class AuditableEntitySaveChangesInterceptor(ICurrentUserProvider currentUser, TimeProvider timeProvider)
    : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        UpdateAuditFields(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        UpdateAuditFields(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void UpdateAuditFields(DbContext? context)
    {
        if (context is null) return;

        DateTimeOffset? now = null;
        Guid? userId = null;

        foreach (var entry in context.ChangeTracker.Entries<Entity>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            // Lazily fetch dependencies only on the first auditable entity
            now ??= timeProvider.GetUtcNow();
            userId ??= currentUser.UserId;

            switch (entry.State)
            {
                case EntityState.Added:
                    entry.Entity.SetCreated(now.Value, userId);
                    break;
                case EntityState.Modified:
                    entry.Entity.SetModified(now.Value, userId);
                    break;
                case EntityState.Detached:
                case EntityState.Unchanged:
                case EntityState.Deleted:
                default:
                    // Intentionally ignored (no-op)
                    break;
            }
        }
    }
}
