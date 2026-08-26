using BuildingBlocks.Identity;
using Catalog.Features.DeletePricingRule;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class DeletePricingRuleEndpoint(IMediator mediator) : Endpoint<DeletePricingRuleRequest, DeletePricingRuleResponse>
{
    public override void Configure()
    {
        Delete("units/{UnitId}/pricing-rules/{PricingRuleId}");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<CatalogGroup>();
        Description(b => b.WithTags("Pricing Rules"));

        Summary(s =>
        {
            s.Summary = "Delete (archive) a pricing rule";
            s.Description = "Soft delete - the rule stops applying to future price resolution going " +
                            "forward. Does not affect the price already locked in on any existing " +
                            "hold/booking. A Host may only delete rules on units under their own property; " +
                            "an Administrator may target any.";
            s.Response<DeletePricingRuleResponse>(200, "Pricing rule deleted.");
            s.Response<ProblemDetails>(404, "Pricing rule not found (or belongs to a different host/unit).");
        });
    }

    public override async Task HandleAsync(DeletePricingRuleRequest req, CancellationToken ct)
    {
        DeletePricingRuleResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
