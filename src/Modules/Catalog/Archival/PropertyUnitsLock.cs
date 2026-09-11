using BuildingBlocks.Persistence;
namespace Catalog.Archival;

/// <summary>
///     Mutual exclusion between archiving a property and adding a unit to it.
///     <para>
///         Archiving a property archives every unit under it, and it finds
///         those units by reading them. A unit created after that read is
///         neither checked nor archived, which leaves a live unit under an
///         archived property - the orphan state <c>UnitLookup</c> throws
///         <c>OrphanedUnitException</c> for, arrived at without anyone doing
///         anything wrong.
///     </para>
///     <para>
///         <see cref="UnitAvailabilityLock"/> cannot cover this. Per-unit locks
///         are taken over the units the archiver read, and the whole problem is
///         a unit that was not in that set. An <c>INSERT</c> has no row to
///         lock, so the thing both sides can agree on has to be the property
///         they are both talking about.
///     </para>
///     <para>
///         <b>Archiving the property takes it exclusively</b>; <b>creating a
///         unit takes it shared</b>, so concurrent creation under one property
///         is not serialised - it only blocks against the archive. Creation
///         must also re-read the property under the lock: ordering the two says
///         nothing about what the other did, and a creation that resolved its
///         property before the lock would otherwise insert under one that has
///         since been archived.
///     </para>
///     <para>
///         Unlike <see cref="UnitAvailabilityLock"/> this is entirely within
///         Catalog - both sides could have used a row lock on <c>properties</c>
///         instead. An advisory lock is still the better fit: the creating side
///         would have to take that row lock <c>FOR UPDATE</c> purely as a
///         signal, blocking unrelated property edits, and the two mechanisms
///         would then disagree about what "the property is busy" means.
///     </para>
/// </summary>
public static class PropertyUnitsLock
{
    public static long KeyFor(Guid propertyId) => AdvisoryLock.KeyFor(Scope, propertyId);

    // Part of the key rather than a label, so it is a wire format between
    // deployments - see AdvisoryLock.KeyFor.
    private const string Scope = "property-units";
}
