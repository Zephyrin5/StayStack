using Identity.Entities;
using Identity.Exceptions;
using Identity.Features.Common;
using Mediator;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
namespace Identity.Features.RefreshToken;

public class RefreshTokenHandler(
    AppIdentityDbContext dbContext,
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

        // Consuming the old token and issuing its replacement must commit or
        // roll back as one unit - otherwise a failure between the two (a
        // transient DB error, FindByIdAsync failing) leaves the old token
        // permanently revoked with no replacement ever issued, stranding the
        // client with no way to refresh. Wrapped in the execution strategy
        // for the same deadlock-retry reason as HoldAvailabilityHandler (see
        // its own comment / docs/adr/0010).
        //
        // UserManager<ApplicationUser> resolves the same scoped
        // AppIdentityDbContext instance injected here (both come from
        // AddEntityFrameworkStores<AppIdentityDbContext>/AddDbContext in the
        // same DI scope), so its calls below participate in this same
        // transaction for free.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // 1. Validate the refresh token (atomically consumes it - see
            // AuthTokenProvider.ValidateRefreshToken).
            RefreshTokenValidationResult validated;
            try
            {
                validated = await authTokenProvider.ValidateRefreshToken(refreshToken, cancellationToken);
            }
            catch
            {
                // The rejection paths (expired/unknown/already-revoked) can
                // themselves have written a durable side effect - reuse
                // detection's whole-family revocation - that must survive
                // regardless of this exception. Only the "consumed
                // successfully, then something downstream failed" path
                // below should roll back the consumption itself.
                await transaction.CommitAsync(cancellationToken);
                throw;
            }

            // 2. Get user roles
            ApplicationUser user = await userManager.FindByIdAsync(validated.UserId.ToString())
                                   ?? throw new UnauthorizedAccessException("Invalid refresh token.");
            var roles = await userManager.GetRolesAsync(user);

            // 3. Generate a brand-new Access Token and Refresh Token pair -
            // the new refresh token stays in the same rotation family as the
            // one just consumed.
            string newAccessToken = authTokenProvider.GenerateJwtToken(user, roles);
            string newRefreshToken = await authTokenProvider.GenerateRefreshToken(
                validated.UserId, validated.FamilyId, validated.TokenId, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            // 4. Return the new token pair
            return new RefreshTokenResponse
            {
                AccessToken = newAccessToken,
                RefreshToken = newRefreshToken
            };
        });
    }
}
