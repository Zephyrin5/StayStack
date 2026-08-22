using BuildingBlocks.Identity;
using FastEndpoints;
using Hosts.Features.CreateHost;
using Mediator;
using Microsoft.AspNetCore.Mvc;
namespace Api.Endpoints.Hosts;

// Distinct from BecomeHost: that one lets an existing authenticated account
// self-service its own upgrade to Host, deriving everything from the
// caller's own identity. This one lets an Administrator create a Host
// record directly - for onboarding a host who doesn't have a linked
// account yet, staff-assisted setup, etc. - with no user linkage at all;
// AdminCreateProperty and BecomeHost's IHostRegistrar path cover linking
// a Host to a user elsewhere, not this endpoint's job.
public class CreateHostEndpoint(IMediator mediator) : Endpoint<CreateHostRequest, CreateHostResponse>
{
    public override void Configure()
    {
        Post("");
        Policies(AuthorizationPolicies.Administrator);
        Group<HostsGroup>();

        Summary(s =>
        {
            s.Summary = "Create a new Host record directly (admin only)";
            s.Description = "Creates a Host with no linked user account - see BecomeHost for the self-service " +
                            "flow that links a Host to the caller's own account instead.";
            s.Response<CreateHostResponse>(200, "Host created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
        });
    }

    public override async Task HandleAsync(CreateHostRequest req, CancellationToken ct)
    {
        CreateHostResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
