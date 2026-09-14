using Identity.Entities;
using System.Security.Claims;
namespace Identity.Features.Common;

public interface IAuthTokenProvider
{
    string GenerateJwtToken(ApplicationUser user, IList<string> roles);

    /// <summary>
    ///     Signs a token that is deliberately not a user identity - no
    ///     <c>sub</c>, no roles, a caller-supplied audience and a short
    ///     lifetime. Used for booking-management sessions.
    ///     <para>
    ///         Generic rather than a GenerateBookingSessionToken: Identity has
    ///         no business knowing what a booking is, and the only thing it
    ///         contributes here is signing. The caller owns the claims and the
    ///         audience; this owns the key, the issuer and the algorithm, so
    ///         there stays exactly one place in the app that signs a JWT.
    ///     </para>
    /// </summary>
    string GenerateScopedToken(string audience, IEnumerable<Claim> claims, TimeSpan lifetime);

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
    ///     Persists <paramref name="token"/> as a new refresh token and returns
    ///     its plaintext. Pass familyId/parentTokenId as null for a fresh sign-in
    ///     (starts a new family); pass the values from a just-validated token to
    ///     rotate within the same family.
    ///     <para>
    ///         The caller supplies the token rather than this minting it - see
    ///         <see cref="IssuedRefreshToken"/>.
    ///     </para>
    /// </summary>
    Task<string> GenerateRefreshToken(
        Guid userId, Guid? familyId, Guid? parentTokenId, IssuedRefreshToken token, CancellationToken cancellationToken);

    /// <summary>
    ///     The user a rotation already committed for, or null.
    ///     <para>
    ///         True only when <paramref name="presentedToken"/> was consumed and
    ///         replaced by exactly <paramref name="replacementId"/>, and that
    ///         replacement is still live. The id is chosen server-side per
    ///         request and never leaves it, so this recognises a retry of the
    ///         same request and nothing else: an attacker replaying a stolen
    ///         token arrives as a new request with a new id, finds nothing, and
    ///         reaches reuse detection as before.
    ///     </para>
    /// </summary>
    Task<Guid?> FindCommittedRotationAsync(
        string presentedToken, Guid replacementId, CancellationToken cancellationToken);

    /// <summary>
    ///     Sign-out: revokes exactly the one token, not its whole family
    ///     (see RevokeFamilyAsync, used only for reuse detection). No-op,
    ///     not an error, if the token doesn't exist or is already revoked -
    ///     sign-out is idempotent.
    /// </summary>
    Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken);
}

public record RefreshTokenValidationResult(Guid UserId, Guid TokenId, Guid FamilyId);
