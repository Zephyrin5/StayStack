using FastEndpoints;
using Identity.Features.Auth.SignUp;
using Identity.Features.SignUp;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Auth;

public class RegisterEndpoint(IMediator mediator) : Endpoint<SignUpRequest, SignUpResponse>
{
    public override void Configure()
    {
        Post("register");
        AllowAnonymous();

        Group<AuthGroup>();

        Summary(s =>
        {
            s.Summary = "Register a new Customer account";
            s.Description = "Creates a new account with the Customer role and signs the caller in immediately, " +
                            "returning an access token and refresh token the same way sign-in does. Hosting is " +
                            "not chosen at registration - see the (planned) BecomeHost endpoint for that.";
            s.ExampleRequest = new SignUpRequest
            {
                Email = "user@example.com",
                Password = "correct-horse-battery-staple",
                ConfirmPassword = "correct-horse-battery-staple"
            };
            s.Response<SignUpResponse>(200, "Account created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(409, "Email already in use.");
        });
    }

    public override async Task HandleAsync(SignUpRequest req, CancellationToken ct)
    {
        SignUpResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
