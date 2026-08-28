using BuildingBlocks.Security;
using Identity.Configurations;
using Identity.Entities;
using Identity.Exceptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text;
namespace Identity.Features.Common;

// See docs/adr/0009 for the full rotation/family/reuse-detection design
// this implements - the comments below cover each piece's local "why",
// the ADR ties them together into the one coherent picture.
public class AuthTokenProvider(
    AppIdentityDbContext dbContext,
    IOptions<AuthTokenConfiguration> jwtSettings,
    TimeProvider timeProvider) : IAuthTokenProvider
{
    private readonly AuthTokenConfiguration _authTokenSettings = jwtSettings.Value;

    public string GenerateJwtToken(ApplicationUser user, IList<string> roles)
    {
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.UniqueName, user.UserName ?? string.Empty),
            new Claim(JwtRegisteredClaimNames.Email, user.Email ?? string.Empty),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));

        // Present only for accounts that have completed BecomeHost - this
        // is the claim CreateProperty/CreateUnit's future "same host
        // tenant" authorization policy checks against. No signature change
        // needed here to support it: GenerateJwtToken already takes the
        // full ApplicationUser, so every caller (SignIn, Register,
        // BecomeHost) gets this for free the moment HostId is set.
        if (user.HostId is not null)
        {
            claims.Add(new Claim("host_id", user.HostId.Value.ToString()));
        }

        SymmetricSecurityKey key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_authTokenSettings.Key));
        SigningCredentials credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        SecurityTokenDescriptor tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = timeProvider.GetUtcNow().UtcDateTime.AddMinutes(_authTokenSettings.AccessTokenLifespanInMinutes),
            SigningCredentials = credentials,
            Issuer = _authTokenSettings.Issuer,
            Audience = _authTokenSettings.Audience
        };

        JsonWebTokenHandler handler = new JsonWebTokenHandler();
        return handler.CreateToken(tokenDescriptor);
    }

    public async Task<string> GenerateRefreshToken(Guid userId, Guid? familyId, Guid? parentTokenId, CancellationToken cancellationToken)
    {
        string newRefreshTokenPlain = SecureToken.Generate();
        string newRefreshTokenHash = SecureToken.Hash(newRefreshTokenPlain);
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        Entities.RefreshToken newRefreshTokenEntity = new Entities.RefreshToken
        {
            TokenHash = newRefreshTokenHash,
            UserId = userId,
            FamilyId = familyId ?? Guid.CreateVersion7(),
            ParentTokenId = parentTokenId,
            CreatedAt = now,
            ExpiresAt = now.AddDays(_authTokenSettings.RefreshTokenLifespanInDays),
            IsRevoked = false
        };

        dbContext.RefreshTokens.Add(newRefreshTokenEntity);
        await dbContext.SaveChangesAsync(cancellationToken);

        if (parentTokenId is not null)
        {
            await dbContext.RefreshTokens
                .Where(rt => rt.Id == parentTokenId)
                .ExecuteUpdateAsync(s => s.SetProperty(rt => rt.ReplacedByTokenId, newRefreshTokenEntity.Id), cancellationToken);
        }

        return newRefreshTokenPlain;
    }

    public async Task<RefreshTokenValidationResult> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken)
    {
        string incomingTokenHash = SecureToken.Hash(refreshToken);
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        // A single conditional UPDATE, not a SELECT-then-check-then-UPDATE:
        // two concurrent callers presenting the same still-valid token can
        // no longer both observe IsRevoked == false and both rotate it -
        // only one UPDATE can match `!rt.IsRevoked` before the other
        // commits, so exactly one succeeds and the other lands in the
        // rows == 0 branch below, where it's correctly classified as reuse
        // rather than silently succeeding a second time.
        int rowsUpdated = await dbContext.RefreshTokens
            .Where(rt => rt.TokenHash == incomingTokenHash && !rt.IsRevoked && rt.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s
                .SetProperty(rt => rt.IsRevoked, true)
                .SetProperty(rt => rt.RevokedAt, now), cancellationToken);

        if (rowsUpdated == 1)
        {
            Entities.RefreshToken consumed = await dbContext.RefreshTokens.AsNoTracking()
                .SingleAsync(rt => rt.TokenHash == incomingTokenHash, cancellationToken);

            return new RefreshTokenValidationResult(consumed.UserId, consumed.Id, consumed.FamilyId);
        }

        // The atomic update matched nothing - find out why, to return the
        // right error (and, for reuse, revoke the family). A second lookup
        // rather than folding this into the UPDATE's WHERE clause, since we
        // need to tell "doesn't exist" apart from "expired" apart from
        // "already revoked" for the caller.
        Entities.RefreshToken? existing = await dbContext.RefreshTokens.AsNoTracking()
            .SingleOrDefaultAsync(rt => rt.TokenHash == incomingTokenHash, cancellationToken);

        if (existing is null)
        {
            throw new InvalidRefreshTokenException();
        }

        // Expiry checked before revocation status: a token that is both
        // revoked and expired (an old, already-rotated token replayed long
        // after its own lifetime ran out) is an expired token, not reuse -
        // checking IsRevoked first would classify that harmless case as an
        // attack and revoke the entire family over it.
        if (existing.ExpiresAt <= now)
        {
            throw new RefreshTokenExpiredException();
        }

        await RevokeFamilyAsync(existing.FamilyId, cancellationToken);
        throw new RefreshTokenReuseDetectedException();
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        string tokenHash = SecureToken.Hash(refreshToken);

        Entities.RefreshToken? storedToken = await dbContext.RefreshTokens
            .SingleOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

        if (storedToken is null || storedToken.IsRevoked)
        {
            return;
        }

        storedToken.IsRevoked = true;
        storedToken.RevokedAt = timeProvider.GetUtcNow().UtcDateTime;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    // Reuse detection revokes only the replayed token's own family (every
    // token descended from one sign-in), not every session the user has
    // anywhere - a stolen token on one device shouldn't sign the user out
    // of an unrelated device's session too.
    private async Task RevokeFamilyAsync(Guid familyId, CancellationToken cancellationToken)
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;

        await dbContext.RefreshTokens
            .Where(rt => rt.FamilyId == familyId && !rt.IsRevoked)
            .ExecuteUpdateAsync(s => s
                .SetProperty(rt => rt.IsRevoked, true)
                .SetProperty(rt => rt.RevokedAt, now), cancellationToken);
    }
}
