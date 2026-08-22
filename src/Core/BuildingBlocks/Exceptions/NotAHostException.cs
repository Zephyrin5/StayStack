using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     The caller has no linked HostId - not "not found", since this is a
///     fact about the caller's own account, not a third party's resource.
/// </summary>
public sealed class NotAHostException()
    : AppException("This account is not linked to a host.", (int)HttpStatusCode.Forbidden);
