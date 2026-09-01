using SeedWork.Enums;
namespace SeedWork.Abstractions;

public abstract class Entity
{
    public Guid Id { get; protected set; }
    public Guid? CreatedBy { get; protected set; }

    // No default here, deliberately - SetCreated always overwrites this with
    // TimeProvider's own value before commit (see the interceptor below), so
    // a DateTime.UtcNow initializer would be dead on every save and a wasted
    // syscall on every entity EF materializes from a query.
    public DateTimeOffset CreatedAt { get; protected set; }
    public Guid? ModifiedBy { get; protected set; }
    public DateTimeOffset? ModifiedAt { get; protected set; }
    public EntityStatus Status { get; protected set; } = EntityStatus.Active;

    // Called only by AuditableEntitySaveChangesInterceptor - kept off each
    // entity's own business API. Internal, not private: the interceptor
    // lives in a different project (Persistence) and needs access without
    // exposing these publicly (see InternalsVisibleTo in Domain.csproj).
    internal void SetCreated(DateTimeOffset createdAt, Guid? createdBy)
    {
        CreatedAt = createdAt;
        CreatedBy = createdBy;
    }

    internal void SetModified(DateTimeOffset modifiedAt, Guid? modifiedBy)
    {
        ModifiedAt = modifiedAt;
        ModifiedBy = modifiedBy;
    }

    public void Archive(DateTimeOffset archivedAt, Guid? archivedBy)
    {
        Status = EntityStatus.Archived;
        SetModified(archivedAt, archivedBy);
    }
}
