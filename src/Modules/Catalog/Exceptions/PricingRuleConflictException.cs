using BuildingBlocks.Exceptions;
using System.Net;
namespace Catalog.Exceptions;

/// <summary>
///     A new or updated pricing rule conflicts with an existing active rule
///     of the same type on the same unit (overlapping date range,
///     overlapping day-of-week set, or a second length-of-stay discount
///     rule) - see the overlap checks in CreatePricingRuleHandler and
///     UpdatePricingRuleHandler. Rejected at write time rather than
///     resolved with a priority/tie-break concept - see docs/adr/0012.
/// </summary>
public sealed class PricingRuleConflictException(string message)
    : AppException(message, (int)HttpStatusCode.Conflict);
