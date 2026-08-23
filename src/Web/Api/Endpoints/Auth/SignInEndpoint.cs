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

// Endpoint<Request, Response> maps the HTTP payload to your data shapes
public class SignInEndpoint(
    IMediator mediator,
    IOptions<AuthTokenConfiguration> tokenSettings) : Endpoint<SignInRequest, SignInResponse>
{
    public override void Configure()
    {
        // Define the HTTP route and method
        Post("sign-in");
        // Allow unauthenticated users to hit this endpoint
        AllowAnonymous();

        Group<AuthGroup>();
        Options(x => x.RequireRateLimiting(ApiServicesRegistration.AuthRateLimitPolicy));

        // Document the endpoint
        Summary(s =>
        {
            s.Summary = "Authenticate user";
            s.Description = "Verifies username and password credentials. On success, returns a JWT access token " +
                            "along with a refresh token. Pass ?useCookies=true to have the refresh token set as " +
                            "an httpOnly cookie instead of returned in the body - the default (no flag) is " +
                            "unchanged token-mode behavior for non-browser clients.";
            s.ExampleRequest = new SignInRequest { Email = "user@example.com", Password = "1234" }; // Pre-populates UI examples
            s.Response<SignInResponse>(200, "Authentication successful");
            s.Response<ValidationProblemDetails>(400, "Validation failure detected");
            s.Response<ProblemDetails>(401, "Invalid credentials");
        });
    }

    public override async Task HandleAsync(SignInRequest req, CancellationToken ct)
    {
        // Execute the business logic via your handler
        SignInResponse result = await mediator.Send(req, ct);

        if (HttpContext.Request.WantsCookieAuth())
        {
            HttpContext.Response.SetRefreshTokenCookie(result.RefreshToken!, tokenSettings.Value);
            result = result with { RefreshToken = null };
        }

        // Send an HTTP 200 OK along with your Response DTO
        await Send.OkAsync(result, ct);
    }
}
