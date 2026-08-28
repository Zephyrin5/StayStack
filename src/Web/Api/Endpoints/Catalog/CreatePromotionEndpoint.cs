using BuildingBlocks.Identity;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Promotions.Features.CreatePromotion;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class CreatePromotionEndpoint(IMediator mediator) : Endpoint<CreatePromotionRequest, CreatePromotionResponse>
{
    public override void Configure()
    {
        Post("promotions");
        Policies(AuthorizationPolicies.Host);
        Group<CatalogGroup>();
        Description(b => b.WithTags("Promotions"));

        Summary(s =>
        {
            s.Summary = "Create a promo code under the caller's own host";
            s.Description = "HostId is derived from the caller's token, never accepted as input - see " +
                            "AdminCreatePromotionEndpoint for the route that lets an Administrator create a " +
                            "host-scoped or platform-wide code. Currency is required for FixedAmount, " +
                            "ignored for Percentage.";
            s.Response<CreatePromotionResponse>(200, "Promo code created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed, or the code is already in use.");
            s.Response<ProblemDetails>(403, "Caller is not linked to a host.");
        });
    }

    public override async Task HandleAsync(CreatePromotionRequest req, CancellationToken ct)
    {
        CreatePromotionResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
