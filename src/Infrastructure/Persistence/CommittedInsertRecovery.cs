using Microsoft.EntityFrameworkCore;
using Npgsql;
using SeedWork.Abstractions;
namespace Persistence;

/// <summary>
///     Recovering an insert that committed and lost its acknowledgement.
///     <para>
///         A bare <c>SaveChangesAsync</c> runs under the execution strategy too,
///         so a retry re-inserts the same caller-chosen id and collides with its
///         own committed row. That surfaces as a unique violation - and handlers
///         that translated every unique violation into a domain error ("code
///         already in use", "already reviewed") answered confidently and wrongly
///         for a row that exists, while those without a catch answered 500.
///     </para>
///     <para>
///         By constraint name, never SqlState alone: only a violation of this
///         entity's own primary key means "our row is already there". Any other
///         unique index is a real conflict and keeps its domain error.
///     </para>
///     <para>
///         The name comes from the EF model, not a literal, so it follows the
///         configuration. What it depends on at the database -
///         that the PK carries that name, and that Postgres reports the PK when a
///         re-inserted row violates it and another unique index at once, which
///         holds only while the PK is the older index - is pinned by
///         PrimaryKeyConstraintTests.
///     </para>
/// </summary>
public static class CommittedInsertRecovery
{
    public static bool IsUniqueViolation(this DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public static bool IsPrimaryKeyViolationOf<TEntity>(this DbUpdateException exception, DbContext context) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } violation
        && violation.ConstraintName == PrimaryKeyNameOf<TEntity>(context);

    public static string PrimaryKeyNameOf<TEntity>(DbContext context) =>
        context.Model.FindEntityType(typeof(TEntity))?.FindPrimaryKey()?.GetName()
        ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no primary key in {context.GetType().Name}.");

    /// <summary>
    ///     The row this operation already committed, when <paramref name="exception"/>
    ///     is a violation of <typeparamref name="TEntity"/>'s own primary key; null
    ///     for any other violation, which the caller should translate as before.
    ///     <para>
    ///         Clears the tracker first. The failed entity is still Added, and
    ///         anything that saved through this context afterwards would try to
    ///         insert it a second time. The read itself is untracked and past the
    ///         soft-delete filter, so it sees the committed row as it is.
    ///     </para>
    /// </summary>
    public static async Task<TEntity?> FindOwnCommittedInsertAsync<TEntity>(
        this DbContext context, DbUpdateException exception, Guid id, CancellationToken cancellationToken)
        where TEntity : Entity
    {
        if (!exception.IsPrimaryKeyViolationOf<TEntity>(context))
        {
            return null;
        }

        context.ChangeTracker.Clear();

        return await context.Set<TEntity>().IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(e => e.Id == id, cancellationToken);
    }
}
