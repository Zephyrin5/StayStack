using BuildingBlocks.Identity;
using Catalog.Features.UpdatePricingRule;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class UpdatePricingRuleEndpoint(IMediator mediator) : Endpoint<UpdatePricingRuleRequest, UpdatePricingRuleResponse>
{
    public override void Configure()
    {
        Put("units/{UnitId}/pricing-rules/{PricingRuleId}");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<CatalogGroup>();

        Summary(s =>
        {
            s.Summary = "Update a pricing rule";
            s.Description = "RuleType cannot be changed - it must match the rule's existing type. A Host " +
                            "may only update rules on units under their own property; an Administrator may " +
                            "target any. Rejected with a 409 if the new values overlap another existing " +
                            "active rule of the same type on this unit.";
            s.Response<UpdatePricingRuleResponse>(200, "Pricing rule updated.");
            s.Response<ValidationProblemDetails>(400, "Validation failed, or RuleType does not match the existing rule.");
            s.Response<ProblemDetails>(404, "Pricing rule not found (or belongs to a different host/unit).");
            s.Response<ProblemDetails>(409, "Conflicts with an existing active rule of the same type.");
        });
    }

    public override async Task HandleAsync(UpdatePricingRuleRequest req, CancellationToken ct)
    {
        UpdatePricingRuleResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
