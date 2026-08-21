using Identity.Entities;
namespace Identity.Features.Auth.Common;

public interface IAuthTokenProvider
{
    string GenerateJwtToken(ApplicationUser user, IList<string> roles);
    Task<Guid> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken);
    Task<string> GenerateRefreshToken(Guid userId, CancellationToken cancellationToken);
}
