using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     The request conflicts with the resource's current state, in a way the
///     caller may be able to resolve by retrying (unlike a validation failure,
///     which needs a different request). Message-only, unlike NotFoundException
///     - the caller needs to know what conflicted, and that varies per call
///     site.
/// </summary>
public sealed class ConflictException(string message)
    : AppException(message, (int)HttpStatusCode.Conflict);
