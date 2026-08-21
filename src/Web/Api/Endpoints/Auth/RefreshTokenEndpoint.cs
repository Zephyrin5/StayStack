using FastEndpoints;
using Identity.Features.Auth.RefreshToken;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;


namespace Api.Endpoints.Auth;

// Endpoint<Request, Response> maps the HTTP payload to your data shapes
public class RefreshTokenEndpoint(IMediator mediator) : Endpoint<RefreshTokenRequest, RefreshTokenResponse>
{
    public override void Configure()
    {
        // Define the HTTP route and method
        Post("refresh-token");
        // Allow unauthenticated users to hit this endpoint
        AllowAnonymous();

        Group<AuthGroup>();

        // Document the endpoint
        Summary(s =>
        {
            s.Summary = "Rotate refresh token";
            s.Description = "Rotates the refresh token and returns a new access and refresh token pair.";
            s.ExampleRequest = new RefreshTokenRequest
                { RefreshToken = "rt_live_9f8d7c6b5a43210fedcba9876543210f19a28b7e" }; // Pre-populates UI examples
            s.Response<RefreshTokenResponse>(200, "Tokens successfully rotated.");
            s.Response<ValidationProblemDetails>(400, "Validation failed or parameters missing.");
            s.Response<ProblemDetails>(401, "Invalid or expired refresh token, or token reuse detected.");
        });
    }

    public override async Task HandleAsync(RefreshTokenRequest req, CancellationToken ct)
    {
        // Execute the business logic via your handler
        RefreshTokenResponse result = await mediator.Send(req, ct);
        // Send an HTTP 200 OK along with your Response DTO
        await Send.OkAsync(result, ct);
    }
}
