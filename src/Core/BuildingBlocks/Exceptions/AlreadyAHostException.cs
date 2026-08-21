using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     BecomeHost called on an account that already has a linked HostId.
/// </summary>
public sealed class AlreadyAHostException()
    : AppException("This account is already linked to a host.", (int)HttpStatusCode.Conflict);
