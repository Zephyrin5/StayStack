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
///         <b>Both sides take it exclusively</b>, and holds used not to. Archival
///         waits for every in-flight hold on the unit and excludes new ones until
///         the archive commits. Holds on one unit also exclude each other, so they
///         queue per unit for the length of one short transaction.
///     </para>
///     <para>
///         Holds took it shared, on the reasoning that the insert "already
///         arbitrates through the exclusion constraint" and a queue would slow the
///         hottest write in the system. The arbitration is what was slow.
///         Concurrent inserters into a GiST exclusion constraint each write their
///         index entry before checking, see each other's uncommitted entry, and
///         wait on each other - a deadlock Postgres only breaks after
///         deadlock_timeout. Measured: ten concurrent hold requests for one unit
///         took over a minute with 43 deadlocks and retry backoff, and at the
///         database directly 10 races produced 70 deadlocks in 70 seconds, every
///         losing inserter a deadlock victim rather than a clean rejection.
///         Serialised, the constraint sees committed rows and rejects at once.
///         HoldExclusionConstraintTests pins both the exactly-one outcome and the
///         absence of deadlocks.
///     </para>
///     <para>
///         Per unit, not global: holds for different units never share a key.
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

    /// <summary>
    ///     The mode taking a hold acquires this lock in - exclusive, see above. One
    ///     definition, shared with HoldExclusionConstraintTests so the test cannot
    ///     drift from the handler.
    /// </summary>
    public const string AcquireForHoldSql = AdvisoryLock.AcquireExclusiveSql;

    /// <summary>The mode archiving a unit acquires this lock in.</summary>
    public const string AcquireForArchivalSql = AdvisoryLock.AcquireExclusiveSql;

    // Part of the key rather than a label, so it is a wire format between
    // deployments - see AdvisoryLock.KeyFor.
    private const string Scope = "unit-availability";
}
