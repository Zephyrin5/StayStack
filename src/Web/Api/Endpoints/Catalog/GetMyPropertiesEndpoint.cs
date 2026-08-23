using BuildingBlocks.Identity;
using Catalog.Features.GetProperties;
using FastEndpoints;
using Hosts.Contracts;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class GetMyPropertiesEndpoint(IMediator mediator, IHostAuthorization hostAuthorization)
    : EndpointWithoutRequest<GetPropertiesResponse>
{
    public override void Configure()
    {
        Get("properties/mine");
        Policies(AuthorizationPolicies.Host);
        Group<CatalogGroup>();

        Summary(s =>
        {
            s.Summary = "List the caller's own properties";
            s.Description = "Requires the caller to be a host - HostId is derived from the caller's token, " +
                            "never accepted as input. Same PropertySummary shape as the public browse endpoint.";
            s.Response<GetPropertiesResponse>(200, "Properties returned.");
            s.Response<ProblemDetails>(403, "Caller is not linked to a host.");
        });
    }

    public override async Task HandleAsync(CancellationToken ct)
    {
        Guid hostId = hostAuthorization.RequireHostId();
        GetPropertiesResponse result = await mediator.Send(new GetPropertiesRequest { HostId = hostId }, ct);
        await Send.OkAsync(result, ct);
    }
}
