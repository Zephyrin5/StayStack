using Api.Endpoints.Hosts;
using BuildingBlocks.Identity;
using Catalog.Features.AdminCreateProperty;
using Catalog.Features.CreateProperty;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

// Deliberately grouped under HostsGroup ("api/hosts"), not CatalogGroup -
// the route is /api/hosts/{HostId}/properties, matching HostsGroup's own
// prefix, even though the handler this dispatches to lives in Catalog.
// Endpoint file location doesn't need to match which module owns the
// handler - Api hosts endpoints wherever the route naturally belongs.
public class AdminCreatePropertyEndpoint(IMediator mediator)
    : Endpoint<AdminCreatePropertyRequest, CreatePropertyResponse>
{
    public override void Configure()
    {
        Post("{HostId}/properties");
        Policies(AuthorizationPolicies.Administrator);
        Group<HostsGroup>();

        Summary(s =>
        {
            s.Summary = "Create a new property under an arbitrary host (admin only)";
            s.Response<CreatePropertyResponse>(200, "Property created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Host not found.");
        });
    }

    public override async Task HandleAsync(AdminCreatePropertyRequest req, CancellationToken ct)
    {
        CreatePropertyResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
