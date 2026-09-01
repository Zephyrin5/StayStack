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
        // roll back as one unit - a failure between the two would leave the
        // old token permanently revoked with no replacement issued,
        // stranding the client. Wrapped in the execution strategy for the
        // same deadlock-retry reason as HoldAvailabilityHandler (docs/adr/0010).
        //
        // UserManager resolves the same scoped AppIdentityDbContext injected
        // here (same DI scope), so its calls below join this same
        // transaction for free.
        IExecutionStrategy strategy = dbContext.Database.CreateExecutionStrategy();

        return await strategy.ExecuteAsync(async () =>
        {
            await using IDbContextTransaction transaction =
                await dbContext.Database.BeginTransactionAsync(cancellationToken);

            // Atomically consumes the token - see AuthTokenProvider.ValidateRefreshToken.
            RefreshTokenValidationResult validated;
            try
            {
                validated = await authTokenProvider.ValidateRefreshToken(refreshToken, cancellationToken);
            }
            catch
            {
                // Rejection paths (expired/unknown/already-revoked) can
                // themselves have written a durable side effect - reuse
                // detection's family revocation - that must survive
                // regardless of this exception. Only a failure after
                // successful consumption should roll back the consumption
                // itself.
                await transaction.CommitAsync(cancellationToken);
                throw;
            }

            // The token consumed above was genuine, but its user is gone -
            // refresh_tokens has no FK to users, so deleting an account
            // leaves its tokens behind and they still validate here.
            //
            // InvalidRefreshTokenException, not UnauthorizedAccessException:
            // the latter is a plain BCL exception, so GlobalExceptionHandler
            // has no arm for it and falls through to a 500. It also makes
            // this response byte-identical to the unknown/expired/revoked
            // paths, which docs/adr/0016's no-enumeration-oracle reasoning
            // wants - a distinguishable answer here would confirm that a
            // token was real and its account since deleted.
            ApplicationUser user = await userManager.FindByIdAsync(validated.UserId.ToString())
                                   ?? throw new InvalidRefreshTokenException();
            var roles = await userManager.GetRolesAsync(user);

            // New refresh token stays in the same rotation family as the
            // one just consumed.
            string newAccessToken = authTokenProvider.GenerateJwtToken(user, roles);
            string newRefreshToken = await authTokenProvider.GenerateRefreshToken(
                validated.UserId, validated.FamilyId, validated.TokenId, cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            return new RefreshTokenResponse
            {
                AccessToken = newAccessToken,
                RefreshToken = newRefreshToken
            };
        });
    }
}
