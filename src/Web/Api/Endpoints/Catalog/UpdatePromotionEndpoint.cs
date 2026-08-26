using BuildingBlocks.Identity;
using Catalog.Features.UpdatePromotion;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class UpdatePromotionEndpoint(IMediator mediator) : Endpoint<UpdatePromotionRequest, UpdatePromotionResponse>
{
    public override void Configure()
    {
        Put("promotions/{PromotionId}");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<CatalogGroup>();
        Description(b => b.WithTags("Promotions"));

        Summary(s =>
        {
            s.Summary = "Update a promo code";
            s.Description = "Code and DiscountType cannot be changed once created. A Host may only update " +
                            "codes they created; an Administrator may target any, including platform-wide " +
                            "codes (which a Host can never touch, even one of their own creation - platform-" +
                            "wide codes have no owning host).";
            s.Response<UpdatePromotionResponse>(200, "Promo code updated.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Promo code not found (or belongs to a different host).");
        });
    }

    public override async Task HandleAsync(UpdatePromotionRequest req, CancellationToken ct)
    {
        UpdatePromotionResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
