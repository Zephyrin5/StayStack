using BuildingBlocks.Identity;
using Catalog.Features.CreatePricingRule;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class CreatePricingRuleEndpoint(IMediator mediator) : Endpoint<CreatePricingRuleRequest, CreatePricingRuleResponse>
{
    public override void Configure()
    {
        Post("units/{UnitId}/pricing-rules");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<CatalogGroup>();
        Description(b => b.WithTags("Pricing Rules"));

        Summary(s =>
        {
            s.Summary = "Create a pricing rule for a unit";
            s.Description = "One of three rule types (DateRangeOverride, DayOfWeekMultiplier, " +
                            "LengthOfStayDiscount) - only the fields for the chosen RuleType are required. " +
                            "A Host may only add rules to units under their own property; an Administrator " +
                            "may target any. Rejected with a 409 if it overlaps an existing active rule of " +
                            "the same type on this unit.";
            s.Response<CreatePricingRuleResponse>(200, "Pricing rule created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Unit not found (or belongs to a different host).");
            s.Response<ProblemDetails>(409, "Conflicts with an existing active rule of the same type.");
        });
    }

    public override async Task HandleAsync(CreatePricingRuleRequest req, CancellationToken ct)
    {
        CreatePricingRuleResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
