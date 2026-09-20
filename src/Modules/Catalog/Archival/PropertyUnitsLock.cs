using BuildingBlocks.Persistence;
namespace Catalog.Archival;

/// <summary>
///     Mutual exclusion between archiving a property and adding a unit to it.
///     <para>
///         Archiving a property archives the units it read. A unit created after that read is neither
///         checked nor archived, leaving a live unit under an archived property - the orphan state
///         <c>UnitLookup</c> throws for, reached without anyone doing anything wrong.
///     </para>
///     <para>
///         <see cref="UnitAvailabilityLock"/> cannot cover it: per-unit locks are taken over the units
///         the archiver read, and the problem is a unit that was not in that set. An INSERT has no row
///         to lock, so both sides agree on the property instead.
///     </para>
///     <para>
///         <b>Archiving takes it exclusively, creating a unit takes it shared</b>, so concurrent
///         creation is not serialised - it only blocks against an archive. Creation re-reads the
///         property under the lock: ordering two operations says nothing about what the other did, and
///         a property resolved before the lock may have been archived since.
///     </para>
/// </summary>
public static class PropertyUnitsLock
{
    public static long KeyFor(Guid propertyId) => AdvisoryLock.KeyFor(Scope, propertyId);

    // Part of the key rather than a label, so it is a wire format between
    // deployments - see AdvisoryLock.KeyFor.
    private const string Scope = "property-units";
}
