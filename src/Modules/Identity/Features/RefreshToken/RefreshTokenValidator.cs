using FastEndpoints;
namespace Identity.Features.RefreshToken;

public sealed class RefreshTokenRequestValidator : Validator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        // No NotEmpty rule: RefreshToken is optional at the DTO level because a
        // cookie-mode caller sends no body. FluentValidation runs before
        // RefreshTokenEndpoint falls back to the httpOnly cookie, so "must have a
        // token from some source" is checked there.
        //
        // No format/length rule either - the token's actual shape is an
        // internal implementation detail. The handler rejects a malformed or
        // expired token, since that check has to hit the RefreshTokens table
        // anyway.
    }
}
