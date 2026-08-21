using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     The requested unit is not available for some or all of the requested
///     date range. Thrown when the database's exclusion constraint rejects
///     a hold attempt - see HoldAvailabilityHandler. This is the entire
///     double-booking guarantee surfacing as a normal HTTP response.
/// </summary>
public sealed class UnitUnavailableException(Guid unitId)
    : AppException($"Unit '{unitId}' is not available for the requested dates.", (int)HttpStatusCode.Conflict);
