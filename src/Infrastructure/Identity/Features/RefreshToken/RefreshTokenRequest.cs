using BuildingBlocks.Observability;
using Mediator;
namespace Identity.Features.Auth.RefreshToken;

public record RefreshTokenRequest : IRequest<RefreshTokenResponse>
{
    // Optional, not required: a cookie-mode caller sends no body at all,
    // relying on RefreshTokenEndpoint to resolve the token from the
    // httpOnly cookie instead. See AuthCookies.GetRefreshTokenFromCookie
    // and RefreshTokenRequestValidator's comment on why the "must have a
    // token from *some* source" check isn't done here.
    [Sensitive] public string? RefreshToken { get; init; }
}
