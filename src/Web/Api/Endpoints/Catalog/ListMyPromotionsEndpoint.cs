using BuildingBlocks.Identity;
using BuildingBlocks.Pagination;
using Catalog.Features;
using Catalog.Features.ListMyPromotions;
using FastEndpoints;
using Mediator;

namespace Api.Endpoints.Catalog;

public class ListMyPromotionsEndpoint(IMediator mediator) : Endpoint<ListMyPromotionsRequest, PagedResponse<PromotionSummary>>
{
    public override void Configure()
    {
        Get("promotions/mine");
        Policies(AuthorizationPolicies.Host);
        Group<CatalogGroup>();

        Summary(s =>
        {
            s.Summary = "List the caller's own host's promo codes";
            s.Description = "Scoped to codes this host created - not platform-wide codes, which the host " +
                            "can't manage even though they'd also apply to this host's units. Paginated - " +
                            "defaults to page 1, 20 per page.";
            s.Response<PagedResponse<PromotionSummary>>(200, "Promo codes returned.");
        });
    }

    public override async Task HandleAsync(ListMyPromotionsRequest req, CancellationToken ct)
    {
        PagedResponse<PromotionSummary> result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
