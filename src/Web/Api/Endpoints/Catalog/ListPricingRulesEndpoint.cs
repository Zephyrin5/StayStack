using BuildingBlocks.Identity;
using Catalog.Features.ListPricingRules;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class ListPricingRulesEndpoint(IMediator mediator) : Endpoint<ListPricingRulesRequest, ListPricingRulesResponse>
{
    public override void Configure()
    {
        Get("units/{UnitId}/pricing-rules");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<CatalogGroup>();
        Description(b => b.WithTags("Pricing Rules"));

        Summary(s =>
        {
            s.Summary = "List a unit's pricing rules";
            s.Description = "Host-facing management view of the raw rules, not resolved prices - see " +
                            "GetPriceCalendarEndpoint for the public resolved-price preview. Unpaged - rule " +
                            "counts per unit are small. A Host may only list rules on units under their own " +
                            "property; an Administrator may target any.";
            s.Response<ListPricingRulesResponse>(200, "Pricing rules returned.");
            s.Response<ProblemDetails>(404, "Unit not found (or belongs to a different host).");
        });
    }

    public override async Task HandleAsync(ListPricingRulesRequest req, CancellationToken ct)
    {
        ListPricingRulesResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
