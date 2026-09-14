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
    //
    // Id is deliberately not set here, and this will be asked again.
    // CreatedAt and CreatedBy are facts about the save - unknown until
    // SavingChanges. Id is a fact about the operation: it has to exist before
    // the first attempt, so a retry after a lost acknowledgement can find the
    // row that attempt committed. The interceptor runs inside every retried
    // delegate, so minting here would put the retry-identity defect behind
    // every entity at once, at the one layer RetryIdentityProtocolTests cannot
    // see. Factories take the id from the caller instead (docs/adr/0025,
    // EntityIdentityProtocolTests) and guard it against Guid.Empty - which
    // also keeps EF from quietly generating a Guid key of its own on Add.
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
