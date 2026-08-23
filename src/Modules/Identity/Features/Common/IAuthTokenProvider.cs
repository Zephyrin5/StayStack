using Identity.Entities;
namespace Identity.Features.Common;

public interface IAuthTokenProvider
{
    string GenerateJwtToken(ApplicationUser user, IList<string> roles);

    /// <summary>
    ///     Atomically consumes the token (flips IsRevoked in a single
    ///     conditional UPDATE, not a separate SELECT-then-UPDATE) and
    ///     returns who it belonged to and which rotation family it's part
    ///     of. Throws InvalidRefreshTokenException (not found),
    ///     RefreshTokenExpiredException, or - if the token was already
    ///     revoked, meaning this is a replay - RefreshTokenReuseDetectedException
    ///     after revoking the rest of its family.
    /// </summary>
    Task<RefreshTokenValidationResult> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken);

    /// <summary>
    ///     Issues a new refresh token. Pass familyId/parentTokenId as null
    ///     for a fresh sign-in (starts a new family); pass the values from
    ///     a just-validated token to rotate within the same family.
    /// </summary>
    Task<string> GenerateRefreshToken(Guid userId, Guid? familyId, Guid? parentTokenId, CancellationToken cancellationToken);

    /// <summary>
    ///     Sign-out: revokes exactly the one token, not its whole family
    ///     (see RevokeFamilyAsync, used only for reuse detection). No-op,
    ///     not an error, if the token doesn't exist or is already revoked -
    ///     sign-out is idempotent.
    /// </summary>
    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);
}

public record RefreshTokenValidationResult(Guid UserId, Guid TokenId, Guid FamilyId);
