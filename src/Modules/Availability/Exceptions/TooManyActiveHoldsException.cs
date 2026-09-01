using BuildingBlocks.Exceptions;
using System.Net;
namespace Availability.Exceptions;

/// <summary>
///     This client network already holds its maximum concurrent live holds.
///     Unlike the per-session-cookie cap this replaced, it counts by a key
///     the caller doesn't supply, so it is what actually bounds the "hold
///     out the whole inventory" attack - the "holds" rate limit bounds
///     request rate, which is a different thing. See docs/adr/0016.
///     Deliberately says "network" rather than naming the address: the
///     message is customer-facing, and it also reaches callers sharing a
///     NAT with whoever consumed the budget.
/// </summary>
public sealed class TooManyActiveHoldsException()
    : AppException("Too many active holds from this network.", (int)HttpStatusCode.TooManyRequests);
