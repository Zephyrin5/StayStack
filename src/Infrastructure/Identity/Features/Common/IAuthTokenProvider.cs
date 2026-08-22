using Identity.Entities;
namespace Identity.Features.Common;

public interface IAuthTokenProvider
{
    string GenerateJwtToken(ApplicationUser user, IList<string> roles);
    Task<Guid> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken);
    Task<string> GenerateRefreshToken(Guid userId, CancellationToken cancellationToken);

    /// <summary>
    ///     Sign-out: revokes exactly the one token, not every session for
    ///     the user (see RevokeAllUserTokensAsync, used only for reuse
    ///     detection). No-op, not an error, if the token doesn't exist or
    ///     is already revoked - sign-out is idempotent.
    /// </summary>
    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);
}
