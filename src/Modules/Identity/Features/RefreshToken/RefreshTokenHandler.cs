using BuildingBlocks.Exceptions;
using Identity.Entities;
using Identity.Features.Common;
using Mediator;
using Microsoft.AspNetCore.Identity;
namespace Identity.Features.RefreshToken;

public class RefreshTokenHandler(
    UserManager<ApplicationUser> userManager,
    IAuthTokenProvider authTokenProvider) : IRequestHandler<RefreshTokenRequest, RefreshTokenResponse>
{
    public async ValueTask<RefreshTokenResponse> Handle(RefreshTokenRequest request, CancellationToken cancellationToken)
    {
        // RefreshTokenEndpoint resolves body-or-cookie before calling
        // here, but RefreshToken is still nullable at this layer (a
        // cookie-mode caller with no cookie and no body reaches here with
        // neither) - same "invalid refresh token" 401 either way, rather
        // than a separate manual check in the endpoint.
        string refreshToken = request.RefreshToken ?? throw new InvalidRefreshTokenException();

        // 1. Validate the refresh token (atomically consumes it - see
        // AuthTokenProvider.ValidateRefreshToken)
        RefreshTokenValidationResult validated = await authTokenProvider.ValidateRefreshToken(refreshToken, cancellationToken);

        // 2. Get user roles
        ApplicationUser user = await userManager.FindByIdAsync(validated.UserId.ToString())
                               ?? throw new UnauthorizedAccessException("Invalid refresh token.");
        var roles = await userManager.GetRolesAsync(user);


        // 3. Generate a brand-new Access Token and Refresh Token pair - the
        // new refresh token stays in the same rotation family as the one
        // just consumed.
        string newAccessToken = authTokenProvider.GenerateJwtToken(user, roles);
        string newRefreshToken = await authTokenProvider.GenerateRefreshToken(
            validated.UserId, validated.FamilyId, validated.TokenId, cancellationToken);


        // 4. Return the new token pair
        return new RefreshTokenResponse
        {
            AccessToken = newAccessToken,
            RefreshToken = newRefreshToken
        };
    }
}
