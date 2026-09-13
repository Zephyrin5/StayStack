using System.Security.Cryptography;
using System.Text;
namespace BuildingBlocks.Persistence;

/// <summary>
///     Postgres transaction-scoped advisory locks, keyed by a scope name and
///     an entity id.
///     <para>
///         The reason to reach for one is always the same: two operations that
///         must exclude each other do not share a row to lock. Either they are
///         in different modules with different DbContexts (see
///         <see cref="UnitAvailabilityLock"/>), or one of them is an
///         <c>INSERT</c>, and a row that does not exist yet cannot be locked.
///     </para>
///     <para>
///         Always <c>pg_advisory_xact_lock</c>, never the session-scoped pair.
///         A transaction-scoped lock is released when the transaction ends
///         however it ends - no unlock call to forget, and no leak on an
///         exception path. It also means <b>taking one outside a transaction
///         is useless</b>: the implicit single-statement transaction ends the
///         moment the <c>SELECT</c> returns, so the lock is gone before the
///         work it was meant to protect begins. Callers assert they are in a
///         transaction rather than assuming it.
///     </para>
/// </summary>
public static class AdvisoryLock
{
    /// <summary>
    ///     A stable 64-bit key for one entity within one scope.
    ///     <para>
    ///         Hashed rather than derived from the Guid's bits directly so the
    ///         scope is part of the key: advisory locks share one global key
    ///         space per database, so an unprefixed id could collide with a
    ///         lock on a different kind of entity that happened to hash the
    ///         same way. A collision costs correctness nothing - the two
    ///         unrelated operations would merely serialise - but it costs
    ///         throughput silently, which is the worst way to pay.
    ///     </para>
    ///     <para>
    ///         <paramref name="scope"/> is part of the persisted meaning of the
    ///         key even though nothing is persisted: two deployments running
    ///         against one database must agree on it, so renaming a scope while
    ///         a mixed set of versions is running means the two sides stop
    ///         excluding each other, silently. Treat these strings as a wire
    ///         format.
    ///     </para>
    /// </summary>
    public static long KeyFor(string scope, Guid id) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"{scope}:{id}")), 0);

    /// <summary>
    ///     Excludes every other holder, shared or exclusive. The writer's side.
    /// </summary>
    public const string AcquireExclusiveSql = "SELECT pg_advisory_xact_lock(@LockKey);";

    /// <summary>
    ///     Excludes only exclusive holders, so operations of this kind still
    ///     run in parallel with each other. The reader's side - used where the
    ///     contended operation is frequent and arbitrates its own conflicts by
    ///     some other means.
    /// </summary>
    public const string AcquireSharedSql = "SELECT pg_advisory_xact_lock_shared(@LockKey);";

    /// <summary>
    ///     Exclusive, without waiting: true if taken, false if somebody else
    ///     holds it. The sweep's side - the advisory counterpart of
    ///     <c>SKIP LOCKED</c>, for a caller that should step over contended work
    ///     and revisit it rather than stall a batch behind it.
    /// </summary>
    public const string TryAcquireExclusiveSql = "SELECT pg_try_advisory_xact_lock(@LockKey);";
}
