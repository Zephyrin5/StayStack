using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     Token expired
/// </summary>
public sealed class RefreshTokenExpiredException(string? message = null)
    : AppException(message ?? "Refresh token has expired.",
        (int)HttpStatusCode.Unauthorized);
