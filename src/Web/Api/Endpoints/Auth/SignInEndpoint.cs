using FastEndpoints;
using Identity.Features.Auth.SignIn;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;


namespace Api.Endpoints.Auth;

// Endpoint<Request, Response> maps the HTTP payload to your data shapes
public class SignInEndpoint(IMediator mediator) : Endpoint<SignInRequest, SignInResponse>
{
    public override void Configure()
    {
        // Define the HTTP route and method
        Post("sign-in");
        // Allow unauthenticated users to hit this endpoint
        AllowAnonymous();

        Group<AuthGroup>();

        // Document the endpoint
        Summary(s =>
        {
            s.Summary = "Authenticate user";
            s.Description = "Verifies username and password credentials. On success, returns a JWT access token along with a refresh token.";
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
        // Send an HTTP 200 OK along with your Response DTO
        await Send.OkAsync(result, ct);
    }
}
