namespace BuildingBlocks.Persistence;

/// <summary>
///     Mutual exclusion between archiving a unit and taking a hold on it, and between concurrent holds
///     on one unit. A row lock on <c>units</c> would work, but it would mean Bookings naming a Catalog
///     table (docs/adr/0004); an advisory lock is keyed on a number, so both sides agree on a name
///     without reaching into the other's schema.
///     <para>
///         <b>Both sides take it exclusively.</b> Holds took it shared, on the reasoning that the
///         exclusion constraint already arbitrates - but that arbitration is by deadlock: concurrent
///         inserters write their GiST entry before checking, then wait on each other until
///         deadlock_timeout. Ten concurrent holds for one unit took over a minute with 43 deadlocks.
///         Serialised, the constraint sees committed rows and rejects at once
///         (docs/adr/0010, HoldExclusionConstraintTests).
///     </para>
///     <para>Per unit: holds for different units never share a key. The mechanics are on
///     <see cref="AdvisoryLock"/>.</para>
/// </summary>
public static class UnitAvailabilityLock
{
    public static long KeyFor(Guid unitId) => AdvisoryLock.KeyFor(Scope, unitId);

    /// <summary>Shared with HoldExclusionConstraintTests, so the test cannot drift from the handler.</summary>
    public const string AcquireForHoldSql = AdvisoryLock.AcquireExclusiveSql;

    /// <summary>The mode archiving a unit acquires this lock in.</summary>
    public const string AcquireForArchivalSql = AdvisoryLock.AcquireExclusiveSql;

    // Part of the key rather than a label, so it is a wire format between
    // deployments - see AdvisoryLock.KeyFor.
    private const string Scope = "unit-availability";
}
