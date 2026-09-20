using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using SeedWork.Abstractions;
using System.Diagnostics.CodeAnalysis;
namespace Persistence;

/// <summary>
///     Recovering an insert that committed and lost its acknowledgement.
///     <para>
///         A bare <c>SaveChangesAsync</c> runs under the execution strategy too, so a retry
///         re-inserts the same caller-chosen id and collides with its own committed row. Handlers that
///         read every unique violation as a domain error ("code already in use", "already reviewed")
///         then answered confidently and wrongly for a row that exists.
///     </para>
///     <para>
///         Only a violation of the entity's own primary key means "our row is
///         already there"; any other unique index is a real conflict. The match
///         depends on two database facts pinned by PrimaryKeyConstraintTests: the
///         primary key carries the model's name, and it is the table's oldest
///         unique index, because Postgres reports the first index a re-inserted
///         row violates.
///     </para>
/// </summary>
public static class CommittedInsertRecovery
{
    /// <summary>
    ///     The row this operation already committed. Call it from a catch that has
    ///     matched a violation of <typeparamref name="TEntity"/>'s own primary key
    ///     (<see cref="ConstraintViolations.IsPrimaryKeyViolationOf{TEntity}"/>).
    ///     <para>
    ///         Clears the tracker first. The failed entity is still Added, and
    ///         anything that saved through this context afterwards would try to
    ///         insert it a second time. The read itself is untracked and past the
    ///         soft-delete filter, so it sees the committed row as it is.
    ///     </para>
    /// </summary>
    public static async Task<TEntity> FindOwnCommittedInsertAsync<[DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors | DynamicallyAccessedMemberTypes.PublicFields | DynamicallyAccessedMemberTypes.NonPublicFields | DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.Interfaces)] TEntity>(
        this DbSet<TEntity> set, Guid id, CancellationToken cancellationToken)
        where TEntity : Entity
    {
        set.GetService<ICurrentDbContext>().Context.ChangeTracker.Clear();

        return await set.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == id, cancellationToken);
    }
}
