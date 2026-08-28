using BuildingBlocks.Exceptions;
using System.Net;
namespace Catalog.Exceptions;

/// <summary>
///     This hold-session already has too many concurrent active
///     (held/booked) holds. A soft, per-session cap - not the thing that
///     actually bounds the "hold out the whole inventory" attack (see
///     docs/adr/0016), just a reasonable ceiling on an ordinary browser
///     session's own accidental accumulation.
/// </summary>
public sealed class TooManyActiveHoldsException()
    : AppException("Too many active holds for this session.", (int)HttpStatusCode.TooManyRequests);
