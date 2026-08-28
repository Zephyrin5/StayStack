using BuildingBlocks.Exceptions;
using System.Net;
namespace Catalog.Exceptions;

/// <summary>
///     A Unit (or a Unit under the Property being archived) has a live
///     booking or an active hold against it - archiving would silently pull
///     the rug out from under a guest mid-stay or mid-checkout. Thrown by
///     DeleteUnitHandler/DeletePropertyHandler before Archive() runs.
/// </summary>
public sealed class UnitHasActiveBookingsException(Guid unitId)
    : AppException($"Unit '{unitId}' has an active booking or hold and cannot be archived.", (int)HttpStatusCode.Conflict);
