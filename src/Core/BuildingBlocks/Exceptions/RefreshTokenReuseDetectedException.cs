using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     Token reuse detected
/// </summary>
public sealed class RefreshTokenReuseDetectedException(string? message = null)
    : AppException(message ?? "Refresh token reuse detected. All sessions have been revoked for security.",
        (int)HttpStatusCode.Unauthorized);
