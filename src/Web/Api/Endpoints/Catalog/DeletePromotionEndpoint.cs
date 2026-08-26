using BuildingBlocks.Identity;
using Catalog.Features.DeletePromotion;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class DeletePromotionEndpoint(IMediator mediator) : Endpoint<DeletePromotionRequest, DeletePromotionResponse>
{
    public override void Configure()
    {
        Delete("promotions/{PromotionId}");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<CatalogGroup>();

        Summary(s =>
        {
            s.Summary = "Delete (archive) a promo code";
            s.Description = "Soft delete - existing redemptions and their audit trail stay intact; the code " +
                            "simply stops resolving for new redemptions going forward. A Host may only " +
                            "delete codes they created; an Administrator may target any.";
            s.Response<DeletePromotionResponse>(200, "Promo code deleted.");
            s.Response<ProblemDetails>(404, "Promo code not found (or belongs to a different host).");
        });
    }

    public override async Task HandleAsync(DeletePromotionRequest req, CancellationToken ct)
    {
        DeletePromotionResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
