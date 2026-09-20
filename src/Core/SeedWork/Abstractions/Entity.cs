using SeedWork.Enums;
namespace SeedWork.Abstractions;

public abstract class Entity
{
    public Guid Id { get; protected set; }
    public Guid? CreatedBy { get; protected set; }

    // No initializer: SetCreated overwrites it before every commit, and one here would also cost a
    // syscall per entity EF materializes.
    public DateTimeOffset CreatedAt { get; protected set; }
    public Guid? ModifiedBy { get; protected set; }
    public DateTimeOffset? ModifiedAt { get; protected set; }
    public EntityStatus Status { get; protected set; } = EntityStatus.Active;

    // Called only by AuditableEntitySaveChangesInterceptor, which lives in another project: internal
    // rather than private, and off each entity's business API.
    //
    // Id is deliberately not set here. CreatedAt and CreatedBy are facts about the save, unknown until
    // SavingChanges; an id is a fact about the operation, which must exist before the first attempt so
    // a retry can find what that attempt committed. Factories take it from the caller (docs/adr/0025,
    // SS0001) and guard against Guid.Empty, which also stops EF generating one on Add.
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
