using BuildingBlocks.Exceptions;
using System.Net;
namespace Identity.Exceptions;

/// <summary>
///     Token reuse detected
/// </summary>
public sealed class RefreshTokenReuseDetectedException(string? message = null)
    : AppException(message ?? "Refresh token reuse detected. This session has been revoked for security.",
        (int)HttpStatusCode.Unauthorized);
