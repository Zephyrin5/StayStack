using SeedWork.Enums;
namespace SeedWork.Abstractions;

public abstract class Entity
{
    public Guid Id { get; protected set; }
    public Guid? CreatedBy { get; protected set; }
    public DateTimeOffset CreatedAt { get; protected set; } = DateTime.UtcNow;
    public Guid? ModifiedBy { get; protected set; }
    public DateTimeOffset? ModifiedAt { get; protected set; }
    public EntityStatus Status { get; protected set; } = EntityStatus.Active;

    // Called by AuditableEntitySaveChangesInterceptor only - not part of
    // any entity's own business API, which is why these live on the base
    // class rather than being duplicated as guard-clause methods on every
    // derived entity. Internal, not private: the interceptor lives in a
    // different project (Persistence) and needs to call these without
    // exposing them to arbitrary application code, so Domain.csproj grants
    // Persistence.csproj access via InternalsVisibleTo (see Domain.csproj).
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
