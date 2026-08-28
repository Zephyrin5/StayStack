using BuildingBlocks.Identity;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Promotions.Features.AdminCreatePromotion;
using Promotions.Features.CreatePromotion;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

// Under CatalogGroup ("api/catalog"), not HostsGroup - unlike
// AdminCreatePropertyEndpoint's route, HostId here is an optional body
// field rather than a route segment (a promotion doesn't always have an
// owning host), so there's no host-scoped URL for this to naturally live
// under. See AdminCreatePromotionRequest's own doc comment.
public class AdminCreatePromotionEndpoint(IMediator mediator)
    : Endpoint<AdminCreatePromotionRequest, CreatePromotionResponse>
{
    public override void Configure()
    {
        Post("promotions/admin");
        Policies(AuthorizationPolicies.Administrator);
        Group<CatalogGroup>();
        Description(b => b.WithTags("Promotions"));

        Summary(s =>
        {
            s.Summary = "Create a promo code under an arbitrary host, or platform-wide (admin only)";
            s.Description = "HostId is optional and explicit - null creates a platform-wide code " +
                            "redeemable against any host's units. Currency is required for FixedAmount, " +
                            "ignored for Percentage.";
            s.Response<CreatePromotionResponse>(200, "Promo code created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed, or the code is already in use.");
            s.Response<ProblemDetails>(404, "HostId was set but does not name a real host.");
        });
    }

    public override async Task HandleAsync(AdminCreatePromotionRequest req, CancellationToken ct)
    {
        CreatePromotionResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
