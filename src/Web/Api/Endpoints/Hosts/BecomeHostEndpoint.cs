using Api.Security;
using FastEndpoints;
using Identity.Configurations;
using Identity.Features.BecomeHost;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Hosts;

public class BecomeHostEndpoint(
    IMediator mediator,
    IOptions<AuthTokenConfiguration> tokenSettings) : Endpoint<BecomeHostRequest, BecomeHostResponse>
{
    public override void Configure()
    {
        Post("become");
        Group<HostsGroup>();

        Summary(s =>
        {
            s.Summary = "Add hosting capability to the caller's existing account";
            s.Description = "Creates a Host record and links it to the caller's account, adding the Host role. " +
                             "Returns reissued tokens carrying the new host_id claim. Pass ?useCookies=true if " +
                             "the caller's session is cookie-mode (see SignInEndpoint) to rotate that cookie " +
                             "instead of returning the new refresh token in the body.";
            s.Response<BecomeHostResponse>(200, "Hosting enabled.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(401, "Not authenticated.");
            s.Response<ProblemDetails>(409, "Account is already linked to a host.");
        });
    }

    public override async Task HandleAsync(BecomeHostRequest req, CancellationToken ct)
    {
        BecomeHostResponse result = await mediator.Send(req, ct);

        if (HttpContext.Request.WantsCookieAuth())
        {
            HttpContext.Response.SetRefreshTokenCookie(result.RefreshToken!, tokenSettings.Value);
            result = result with { RefreshToken = null };
        }

        await Send.OkAsync(result, ct);
    }
}
