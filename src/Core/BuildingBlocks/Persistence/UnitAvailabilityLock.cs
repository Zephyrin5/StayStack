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
///         <b>Archival takes it exclusively</b>
///         (<see cref="AdvisoryLock.AcquireExclusiveSql"/>), waiting for every
///         in-flight hold on the unit and excluding new ones until the archive
///         commits. <b>Taking a hold takes it shared</b>
///         (<see cref="AdvisoryLock.AcquireSharedSql"/>): holds on one unit do
///         not block each other - that path already arbitrates through the
///         exclusion constraint, and serialising it would put a queue on the
///         hottest write in the system to defend against an operation a host
///         performs by hand.
///     </para>
///     <para>
///         The mechanics - transaction scope, why the scope string is part of
///         the key - live on <see cref="AdvisoryLock"/>. This type exists so
///         that both sides have one name to agree on.
///     </para>
/// </summary>
public static class UnitAvailabilityLock
{
    public static long KeyFor(Guid unitId) => AdvisoryLock.KeyFor(Scope, unitId);

    // Part of the key rather than a label, so it is a wire format between
    // deployments - see AdvisoryLock.KeyFor.
    private const string Scope = "unit-availability";
}
