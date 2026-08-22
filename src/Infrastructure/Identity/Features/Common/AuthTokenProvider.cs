using BuildingBlocks.Exceptions;
using Identity.Configurations;
using Identity.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
namespace Identity.Features.Common;

public class AuthTokenProvider(AppIdentityDbContext dbContext, IOptions<AuthTokenConfiguration> jwtSettings) : IAuthTokenProvider
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
            Expires = DateTime.UtcNow.AddMinutes(_authTokenSettings.AccessTokenLifespanInMinutes),
            SigningCredentials = credentials,
            Issuer = _authTokenSettings.Issuer,
            Audience = _authTokenSettings.Audience
        };

        JsonWebTokenHandler handler = new JsonWebTokenHandler();
        return handler.CreateToken(tokenDescriptor);
    }

    public async Task<string> GenerateRefreshToken(Guid userId, CancellationToken cancellationToken)
    {
        byte[] randomNumber = new byte[64];
        using RandomNumberGenerator rng = RandomNumberGenerator.Create();
        rng.GetBytes(randomNumber);
        string newRefreshTokenPlain = Convert.ToBase64String(randomNumber);

        string newRefreshTokenHash = HashToken(newRefreshTokenPlain);

        Entities.RefreshToken newRefreshTokenEntity = new Entities.RefreshToken
        {
            TokenHash = newRefreshTokenHash,
            UserId = userId,
            ExpiresAt = DateTime.UtcNow.AddDays(_authTokenSettings.RefreshTokenLifespanInDays),
            IsRevoked = false
        };

        dbContext.RefreshTokens.Add(newRefreshTokenEntity);
        await dbContext.SaveChangesAsync(cancellationToken);

        return newRefreshTokenPlain;
    }

    public async Task<Guid> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken)
    {
        string incomingTokenHash = HashToken(refreshToken);

        Entities.RefreshToken? storedToken = await dbContext.RefreshTokens
            .SingleOrDefaultAsync(rt => rt.TokenHash == incomingTokenHash, cancellationToken);

        if (storedToken == null)
        {
            throw new InvalidRefreshTokenException();
        }

        if (storedToken.IsRevoked)
        {
            await RevokeAllUserTokensAsync(storedToken.UserId, cancellationToken);
            throw new RefreshTokenReuseDetectedException();
        }

        if (storedToken.ExpiresAt <= DateTime.UtcNow)
        {
            throw new RefreshTokenExpiredException();
        }

        storedToken.IsRevoked = true;
        await dbContext.SaveChangesAsync(cancellationToken);

        return storedToken.UserId;
    }

    public async Task RevokeRefreshTokenAsync(string refreshToken, CancellationToken cancellationToken)
    {
        string tokenHash = HashToken(refreshToken);

        Entities.RefreshToken? storedToken = await dbContext.RefreshTokens
            .SingleOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);

        if (storedToken is null || storedToken.IsRevoked)
        {
            return;
        }

        storedToken.IsRevoked = true;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string HashToken(string token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(token);
        byte[] hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }

    private async Task RevokeAllUserTokensAsync(Guid userId, CancellationToken cancellationToken)
    {
        var activeTokens = await dbContext.RefreshTokens
            .Where(rt => rt.UserId == userId && !rt.IsRevoked)
            .ToListAsync(cancellationToken);

        foreach (Entities.RefreshToken token in activeTokens)
        {
            token.IsRevoked = true;
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
