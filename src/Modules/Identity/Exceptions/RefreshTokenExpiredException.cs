using BuildingBlocks.Exceptions;
using System.Net;
namespace Identity.Exceptions;

/// <summary>
///     Token expired
/// </summary>
public sealed class RefreshTokenExpiredException(string? message = null)
    : AppException(message ?? "Refresh token has expired.",
        (int)HttpStatusCode.Unauthorized);
