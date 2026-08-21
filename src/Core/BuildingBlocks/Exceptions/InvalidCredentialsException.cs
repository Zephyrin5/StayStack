using System.Net;
namespace BuildingBlocks.Exceptions;

/// <summary>
///     Authentication failed. Deliberately generic message regardless of
///     whether the username didn't exist or the password was wrong - do not
///     give an attacker a way to enumerate valid usernames via response
///     differences.
/// </summary>
public sealed class InvalidCredentialsException()
    : AppException("Invalid credentials.", (int)HttpStatusCode.Unauthorized);
