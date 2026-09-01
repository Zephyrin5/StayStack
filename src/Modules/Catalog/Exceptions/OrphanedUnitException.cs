namespace Catalog.Exceptions;

/// <summary>
///     A Unit whose Property row does not exist. Deliberately <b>not</b> an
///     AppException: GlobalExceptionHandler's fallback arm renders it a
///     generic 500, which is the right shape for a data-integrity violation -
///     nothing the caller sent is wrong, and no retry or different input
///     fixes it.
///     <para>
///         Not reachable through archival: DeletePropertyHandler is the only
///         caller of Property.Archive and archives every unit beneath the
///         property in the same SaveChangesAsync, so a soft-deleted property
///         never leaves live units behind. This fires only for a hard-deleted
///         or never-existent Property row - and, by design, would fire loudly
///         if some future path ever archived a property without its units.
///     </para>
/// </summary>
public sealed class OrphanedUnitException(Guid unitId, Guid propertyId)
    : Exception($"Unit '{unitId}' references property '{propertyId}', which does not exist. " +
                "Its host and time zone cannot be resolved.");
