using Api.Security;
using FastEndpoints;
using Identity.Configurations;
using Identity.Features.SignIn;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;


namespace Api.Endpoints.Auth;

public class SignInEndpoint(
    IMediator mediator,
    IOptions<AuthTokenConfiguration> tokenSettings,
    TimeProvider timeProvider) : Endpoint<SignInRequest, SignInResponse>
{
    public override void Configure()
    {
        Post("sign-in");
        AllowAnonymous();

        Group<AuthGroup>();
        Options(x => x.RequireRateLimiting(ApiServicesRegistration.AuthRateLimitPolicy));

        Summary(s =>
        {
            s.Summary = "Authenticate user";
            s.Description = "Verifies username and password credentials. On success, returns a JWT access token " +
                            "along with a refresh token. Pass ?useCookies=true to have the refresh token set as " +
                            "an httpOnly cookie instead of returned in the body - the default (no flag) is " +
                            "unchanged token-mode behavior for non-browser clients.";
            s.ExampleRequest = new SignInRequest { Email = "user@example.com", Password = "1234" };
            s.Response<SignInResponse>(200, "Authentication successful");
            s.Response<ValidationProblemDetails>(400, "Validation failure detected");
            s.Response<ProblemDetails>(401, "Invalid credentials");
        });
    }

    public override async Task HandleAsync(SignInRequest req, CancellationToken ct)
    {
        SignInResponse result = await mediator.Send(req, ct);

        if (HttpContext.Request.WantsCookieAuth())
        {
            HttpContext.Response.SetRefreshTokenCookie(result.RefreshToken!, tokenSettings.Value, timeProvider);
            result = result with { RefreshToken = null };
        }

        await Send.OkAsync(result, ct);
    }
}
