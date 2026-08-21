using FastEndpoints;
using FluentValidation;
namespace Identity.Features.Auth.RefreshToken;

public sealed class RefreshTokenRequestValidator : Validator<RefreshTokenRequest>
{
    public RefreshTokenRequestValidator()
    {
        RuleFor(x => x.RefreshToken)
            .NotEmpty();

        // No format/length rule - the token's actual shape is an
        // internal implementation detail (opaque string vs GUID vs
        // whatever the rotation scheme lands on). The handler is the
        // right place to reject a malformed or expired token, since
        // that check has to hit the RefreshTokens table anyway.
    }
}
