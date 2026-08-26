using BuildingBlocks.Identity;
using BuildingBlocks.Pagination;
using Catalog.Features.GetHostProperties;
using Catalog.Features.GetProperties;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Hosts;

public class GetHostPropertiesEndpoint(IMediator mediator) : Endpoint<GetHostPropertiesRequest, PagedResponse<PropertySummary>>
{
    public override void Configure()
    {
        Get("{HostId}/properties");
        Policies(AuthorizationPolicies.Administrator);
        Group<HostsGroup>();

        Summary(s =>
        {
            s.Summary = "List a specific host's properties (admin-only)";
            s.Description = "The read counterpart to POST /api/hosts/{hostId}/properties (AdminCreateProperty) " +
                            "- lets an Administrator browse a host's portal the way that host would see it. " +
                            "Paginated - defaults to page 1, 20 per page.";
            s.Response<PagedResponse<PropertySummary>>(200, "Properties returned.");
            s.Response<ProblemDetails>(404, "Host not found.");
        });
    }

    public override async Task HandleAsync(GetHostPropertiesRequest req, CancellationToken ct)
    {
        PagedResponse<PropertySummary> result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
