using Api.Security;
using FastEndpoints;
using Identity.Features.Auth.SignOut;
using Mediator;
namespace Api.Endpoints.Auth;

public class SignOutEndpoint(IMediator mediator) : Endpoint<SignOutRequest, SignOutResponse>
{
    public override void Configure()
    {
        Post("sign-out");
        // A caller with an already-expired access token should still be
        // able to sign out cleanly - this only ever touches the refresh
        // token/cookie, never anything requiring a valid access token.
        AllowAnonymous();

        Group<AuthGroup>();

        Summary(s =>
        {
            s.Summary = "Sign out";
            s.Description = "Revokes the caller's refresh token (body or, for a cookie-mode session, the " +
                            "httpOnly cookie - see SignInEndpoint) and clears that cookie if present. Always " +
                            "succeeds - an already-invalid or missing token has nothing left to revoke.";
            s.Response<SignOutResponse>(200, "Signed out.");
        });
    }

    public override async Task HandleAsync(SignOutRequest req, CancellationToken ct)
    {
        string? refreshToken = req.RefreshToken ?? HttpContext.Request.GetRefreshTokenFromCookie();

        SignOutResponse result = await mediator.Send(new SignOutRequest { RefreshToken = refreshToken }, ct);

        // Unconditional, not just when a cookie was found - a token-mode
        // caller that happens to pass ?useCookies=true here (or just has
        // a stale cookie from a previous session) still gets it cleared.
        HttpContext.Response.DeleteRefreshTokenCookie();

        await Send.OkAsync(result, ct);
    }
}
