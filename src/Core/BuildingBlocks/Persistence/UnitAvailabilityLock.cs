using System.Security.Cryptography;
using System.Text;
namespace BuildingBlocks.Persistence;

/// <summary>
///     Mutual exclusion between archiving a unit and taking a hold on it.
///     <para>
///         The two are in different modules with different DbContexts, so
///         they are on different connections and in different transactions.
///         Catalog checks "no active bookings or holds", then archives; there
///         is no synchronisation spanning the two, so Bookings can insert a
///         hold in the gap and the unit is archived with live inventory
///         against it.
///     </para>
///     <para>
///         A row lock on <c>units</c> would work - row locks are database-wide,
///         not context-wide - but it would mean Bookings naming a Catalog
///         table, which is the coupling docs/adr/0004 exists to prevent and
///         the one the Availability merge spent a whole phase removing. A
///         Postgres advisory lock is keyed on a number rather than on anyone's
///         row, so both sides can agree on a name without either reaching into
///         the other's schema.
///     </para>
///     <para>
///         Taken with <c>pg_advisory_xact_lock</c>, so it is released when the
///         transaction ends however it ends - no unlock call to forget, and no
///         leak on an exception path.
///     </para>
/// </summary>
public static class UnitAvailabilityLock
{
    /// <summary>
    ///     A stable 64-bit key for one unit.
    ///     <para>
    ///         Hashed rather than derived from the Guid's bits directly so the
    ///         namespace prefix is part of it: advisory locks share one global
    ///         key space per database, so an unprefixed id could collide with
    ///         some future lock on a different kind of entity that happened to
    ///         hash the same way. Collisions cost correctness nothing - two
    ///         unrelated operations would merely serialise - but they cost
    ///         throughput silently, which is the worst way to pay.
    ///     </para>
    /// </summary>
    public static long KeyFor(Guid unitId) =>
        BitConverter.ToInt64(SHA256.HashData(Encoding.UTF8.GetBytes($"unit-availability:{unitId}")), 0);

    /// <summary>
    ///     Exclusive: archival. Waits for every in-flight hold on this unit and
    ///     excludes new ones until the archiving transaction commits.
    /// </summary>
    public const string AcquireExclusiveSql = "SELECT pg_advisory_xact_lock(@LockKey);";

    /// <summary>
    ///     Shared: taking a hold. Holds on one unit do not block each other -
    ///     that path already arbitrates through the exclusion constraint, and
    ///     serialising it would put a queue on the hottest write in the system
    ///     to defend against an operation that happens by hand. They only block
    ///     archival, which is the whole point.
    /// </summary>
    public const string AcquireSharedSql = "SELECT pg_advisory_xact_lock_shared(@LockKey);";
}
