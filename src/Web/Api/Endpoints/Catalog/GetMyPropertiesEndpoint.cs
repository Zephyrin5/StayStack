using BuildingBlocks.Identity;
using BuildingBlocks.Pagination;
using Catalog.Features.GetMyProperties;
using Catalog.Features.GetProperties;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class GetMyPropertiesEndpoint(IMediator mediator)
    : Endpoint<GetMyPropertiesRequest, PagedResponse<PropertySummary>>
{
    public override void Configure()
    {
        Get("properties/mine");
        Policies(AuthorizationPolicies.Host);
        Group<CatalogGroup>();
        Description(b => b.WithTags("Properties"));

        Summary(s =>
        {
            s.Summary = "List the caller's own properties";
            s.Description = "Requires the caller to be a host - HostId is derived from the caller's token, " +
                            "never accepted as input. Same PropertySummary shape as the public browse endpoint. " +
                            "Paginated (defaults to page 1, 20 per page).";
            s.Response<PagedResponse<PropertySummary>>(200, "Properties returned.");
            s.Response<ProblemDetails>(403, "Caller is not linked to a host.");
        });
    }

    public override async Task HandleAsync(GetMyPropertiesRequest req, CancellationToken ct)
    {
        PagedResponse<PropertySummary> result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
