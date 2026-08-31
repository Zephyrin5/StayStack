using Api.Security;
using FastEndpoints;
using Identity.Configurations;
using Identity.Features.RefreshToken;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;


namespace Api.Endpoints.Auth;

public class RefreshTokenEndpoint(
    IMediator mediator,
    IOptions<AuthTokenConfiguration> tokenSettings,
    TimeProvider timeProvider) : Endpoint<RefreshTokenRequest, RefreshTokenResponse>
{
    public override void Configure()
    {
        Post("refresh-token");
        AllowAnonymous();

        Group<AuthGroup>();
        Options(x => x.RequireRateLimiting(ApiServicesRegistration.AuthRateLimitPolicy));

        Summary(s =>
        {
            s.Summary = "Rotate refresh token";
            s.Description = "Rotates the refresh token and returns a new access and refresh token pair. A " +
                            "cookie-mode caller (see SignInEndpoint) can omit the body entirely and pass " +
                            "?useCookies=true - the token is read from the httpOnly cookie and the response " +
                            "rotates that cookie instead of returning the new refresh token in the body.";
            s.ExampleRequest = new RefreshTokenRequest
                { RefreshToken = "rt_live_9f8d7c6b5a43210fedcba9876543210f19a28b7e" };
            s.Response<RefreshTokenResponse>(200, "Tokens successfully rotated.");
            s.Response<ValidationProblemDetails>(400, "Validation failed or parameters missing.");
            s.Response<ProblemDetails>(401, "Invalid or expired refresh token, or token reuse detected.");
        });
    }

    public override async Task HandleAsync(RefreshTokenRequest req, CancellationToken ct)
    {
        bool cookieAuth = HttpContext.Request.WantsCookieAuth();
        // Empty/whitespace treated the same as missing, not as a literal
        // "" token to look up - a client sending {"refreshToken":""} means
        // the same thing as sending no body at all.
        string? refreshToken = string.IsNullOrWhiteSpace(req.RefreshToken)
            ? HttpContext.Request.GetRefreshTokenFromCookie()
            : req.RefreshToken;

        // A null token here reaches RefreshTokenHandler's own guard, which
        // throws the same InvalidRefreshTokenException (401) a bad token
        // would.
        RefreshTokenResponse result = await mediator.Send(new RefreshTokenRequest { RefreshToken = refreshToken }, ct);

        if (cookieAuth)
        {
            HttpContext.Response.SetRefreshTokenCookie(result.RefreshToken!, tokenSettings.Value, timeProvider);
            result = result with { RefreshToken = null };
        }

        await Send.OkAsync(result, ct);
    }
}
