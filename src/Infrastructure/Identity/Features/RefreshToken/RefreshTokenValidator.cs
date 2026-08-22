using FastEndpoints;
namespace Identity.Features.Auth.RefreshToken;

public sealed class RefreshTokenRequestValidator : Validator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        // No NotEmpty rule here (used to be one) - RefreshToken is now
        // optional at the DTO level because a cookie-mode caller sends no
        // body at all. FluentValidation runs before HandleAsync, too early
        // to know whether RefreshTokenEndpoint will fall back to reading
        // the httpOnly cookie, so "must have a token from *some* source"
        // is checked there instead, after the fallback is resolved.
        //
        // No format/length rule either - the token's actual shape is an
        // internal implementation detail (opaque string vs GUID vs
        // whatever the rotation scheme lands on). The handler is the
        // right place to reject a malformed or expired token, since
        // that check has to hit the RefreshTokens table anyway.
    }
}
