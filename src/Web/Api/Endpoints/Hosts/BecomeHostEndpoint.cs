using FastEndpoints;
using Identity.Features.BecomeHost;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Hosts;

public class BecomeHostEndpoint(IMediator mediator) : Endpoint<BecomeHostRequest, BecomeHostResponse>
{
    public override void Configure()
    {
        Post("become");
        Group<HostsGroup>();

        Summary(s =>
        {
            s.Summary = "Add hosting capability to the caller's existing account";
            s.Description = "Creates a Host record and links it to the caller's account, adding the Host role. " +
                             "Returns reissued tokens carrying the new host_id claim.";
            s.Response<BecomeHostResponse>(200, "Hosting enabled.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(401, "Not authenticated.");
            s.Response<ProblemDetails>(409, "Account is already linked to a host.");
        });
    }

    public override async Task HandleAsync(BecomeHostRequest req, CancellationToken ct)
    {
        BecomeHostResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
