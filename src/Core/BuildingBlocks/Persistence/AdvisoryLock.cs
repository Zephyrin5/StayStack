using System.Security.Cryptography;
using System.Text;
namespace BuildingBlocks.Persistence;

/// <summary>
///     Postgres transaction-scoped advisory locks, keyed by a scope name and an entity id. Reached for
///     when two operations that must exclude each other share no row to lock - one of them is an
///     INSERT, or they are in different modules (<see cref="UnitAvailabilityLock"/>).
///     <para>
///         Always <c>pg_advisory_xact_lock</c>, never the session-scoped pair: it is released when the
///         transaction ends however it ends. Which also means <b>taking one outside a transaction is
///         useless</b> - the implicit single-statement transaction ends as the SELECT returns, before
///         the work it guards begins. Callers assert they are in one.
///     </para>
/// </summary>
public static class AdvisoryLock
{
    /// <summary>
    ///     A stable 64-bit key for one entity within one scope. Hashed with the scope rather than
    ///     taken from the Guid's bits: advisory locks share one key space per database, and an
    ///     unprefixed id could collide with a lock on another kind of entity - which costs correctness
    ///     nothing and throughput silently.
    ///     <para>
    ///         <paramref name="scope"/> is a wire format between deployments: renaming one while a
    ///         mixed set of versions runs makes the two sides stop excluding each other.
    ///     </para>
    /// </summary>
    public static long KeyFor(string scope, Guid id) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}:{id}")), 0);

    /// <summary>Excludes every other holder, shared or exclusive. The writer's side.</summary>
    public const string AcquireExclusiveSql = "SELECT pg_advisory_xact_lock(@LockKey);";

    /// <summary>
    ///     Excludes only exclusive holders, so operations of this kind still run in parallel. The
    ///     reader's side, where the contended operation arbitrates its own conflicts some other way.
    /// </summary>
    public const string AcquireSharedSql = "SELECT pg_advisory_xact_lock_shared(@LockKey);";

    /// <summary>
    ///     Exclusive, without waiting: true if taken. The sweep's side, the advisory counterpart of
    ///     SKIP LOCKED, for a caller that steps over contended work rather than stalling a batch.
    /// </summary>
    public const string TryAcquireExclusiveSql = "SELECT pg_try_advisory_xact_lock(@LockKey);";
}
