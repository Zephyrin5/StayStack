using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     Invalid refresh token
/// </summary>
public sealed class InvalidRefreshTokenException(string? message = null)
    : AppException(message ?? "Invalid refresh token.",
        (int)HttpStatusCode.Unauthorized);
