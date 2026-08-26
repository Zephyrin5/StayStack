using BuildingBlocks.Identity;
using BuildingBlocks.Pagination;
using Catalog.Features;
using Catalog.Features.GetHostPromotions;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Hosts;

public class GetHostPromotionsEndpoint(IMediator mediator) : Endpoint<GetHostPromotionsRequest, PagedResponse<PromotionSummary>>
{
    public override void Configure()
    {
        Get("{HostId}/promotions");
        Policies(AuthorizationPolicies.Administrator);
        Group<HostsGroup>();

        Summary(s =>
        {
            s.Summary = "List a specific host's promo codes (admin-only)";
            s.Description = "The read counterpart to POST /api/catalog/promotions/admin - lets an " +
                            "Administrator browse a host's portal the way that host would see it. " +
                            "Paginated - defaults to page 1, 20 per page.";
            s.Response<PagedResponse<PromotionSummary>>(200, "Promo codes returned.");
            s.Response<ProblemDetails>(404, "Host not found.");
        });
    }

    public override async Task HandleAsync(GetHostPromotionsRequest req, CancellationToken ct)
    {
        PagedResponse<PromotionSummary> result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
