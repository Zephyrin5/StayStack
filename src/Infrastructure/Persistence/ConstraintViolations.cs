using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Diagnostics.CodeAnalysis;
namespace Persistence;

/// <summary>
///     Recognises a PostgreSQL integrity violation by the constraint that raised
///     it. The only code in src that inspects one; ConstraintViolationProtocolTests
///     enforces that.
///     <para>
///         Matching is by constraint name because constraints on one table share a
///         SQLSTATE and mean different things: a primary key colliding with this
///         operation's own committed row, a unique index held by another row, an
///         index whose collision is transient. A catch on SQLSTATE alone gives one
///         answer to all of them.
///     </para>
///     <para>
///         Callers pass names from a shared source - the EF model for primary
///         keys, and constants that the entity configuration or migration also
///         uses for indexes and constraints.
///     </para>
/// </summary>
// The one file allowed to read a PostgresException's ConstraintName and SqlState: src/BannedSymbols.txt
// bans both everywhere else, so a catch on SQLSTATE alone cannot be written outside this file.
#pragma warning disable RS0030
public static class ConstraintViolations
{
    public static bool IsViolationOf(this Exception exception, string constraintName) =>
        IntegrityViolation(exception)?.ConstraintName == constraintName;

    public static bool IsViolationOfAny(this Exception exception, params string[] constraintNames) =>
        IntegrityViolation(exception)?.ConstraintName is { } name && constraintNames.Contains(name);

    public static bool IsPrimaryKeyViolationOf<TEntity>(this Exception exception, DbSet<TEntity> set)
        where TEntity : class =>
        exception.IsViolationOf(PrimaryKeyNameOf(set));

    public static string PrimaryKeyNameOf<TEntity>(DbSet<TEntity> set)
        where TEntity : class =>
        set.EntityType.FindPrimaryKey()?.GetName()
        ?? throw new InvalidOperationException($"{typeof(TEntity).Name} has no primary key.");

    private const string IntegrityViolationClass = "23";

    // SQLSTATE class 23: integrity constraint violation. Reached directly from
    // Dapper, or as the inner exception of an EF save.
    private static PostgresException? IntegrityViolation(Exception exception) =>
        (exception as PostgresException ?? exception.InnerException as PostgresException) is { } postgres
        && postgres.SqlState.StartsWith(IntegrityViolationClass, StringComparison.Ordinal)
        && postgres.ConstraintName is not null
            ? postgres
            : null;
}
#pragma warning restore RS0030
