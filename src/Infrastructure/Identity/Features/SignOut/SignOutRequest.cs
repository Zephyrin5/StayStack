using BuildingBlocks.Observability;
using Mediator;
namespace Identity.Features.Auth.SignOut;

public record SignOutRequest : IRequest<SignOutResponse>
{
    // Optional, same reasoning as RefreshTokenRequest - a cookie-mode
    // caller sends no body, SignOutEndpoint resolves this from the
    // httpOnly cookie instead. Unlike refresh, a missing token here isn't
    // an error: sign-out is idempotent, "nothing to revoke" is a valid
    // outcome, not a failure.
    [Sensitive] public string? RefreshToken { get; init; }
}
