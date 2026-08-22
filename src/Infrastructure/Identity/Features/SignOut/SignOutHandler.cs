using Identity.Features.Auth.Common;
using Mediator;
namespace Identity.Features.Auth.SignOut;

public class SignOutHandler(IAuthTokenProvider authTokenProvider) : IRequestHandler<SignOutRequest, SignOutResponse>
{
    public async ValueTask<SignOutResponse> Handle(SignOutRequest request, CancellationToken cancellationToken)
    {
        // No-op, not an error, if RefreshToken is null/not found/already
        // revoked - see RevokeRefreshTokenAsync and SignOutRequest's own
        // comment on why. Sign-out always succeeds from the caller's
        // point of view.
        if (!string.IsNullOrWhiteSpace(request.RefreshToken))
        {
            await authTokenProvider.RevokeRefreshTokenAsync(request.RefreshToken, cancellationToken);
        }

        return new SignOutResponse();
    }
}
