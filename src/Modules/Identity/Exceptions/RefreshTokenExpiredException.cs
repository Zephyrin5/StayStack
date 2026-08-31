using BuildingBlocks.Exceptions;
using System.Net;
namespace Identity.Exceptions;

public sealed class RefreshTokenExpiredException(string? message = null)
    : AppException(message ?? "Refresh token has expired.",
        (int)HttpStatusCode.Unauthorized);
