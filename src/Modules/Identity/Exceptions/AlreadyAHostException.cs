using BuildingBlocks.Exceptions;
using System.Net;
namespace Identity.Exceptions;

/// <summary>
///     BecomeHost called on an account that already has a linked HostId.
/// </summary>
public sealed class AlreadyAHostException()
    : AppException("This account is already linked to a host.", (int)HttpStatusCode.Conflict);
