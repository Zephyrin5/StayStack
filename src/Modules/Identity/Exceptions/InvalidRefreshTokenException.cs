using BuildingBlocks.Exceptions;
using System.Net;
namespace Identity.Exceptions;

/// <summary>
///     Invalid refresh token
/// </summary>
public sealed class InvalidRefreshTokenException(string? message = null)
    : AppException(message ?? "Invalid refresh token.",
        (int)HttpStatusCode.Unauthorized);
