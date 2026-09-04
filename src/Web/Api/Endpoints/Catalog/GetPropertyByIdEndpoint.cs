using Catalog.Features.GetPropertyById;
using Api;
using FastEndpoints;
using Microsoft.AspNetCore.RateLimiting;
using Mediator;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class GetPropertyByIdEndpoint(IMediator mediator) : Endpoint<GetPropertyByIdRequest, GetPropertyByIdResponse>
{
    public override void Configure()
    {
        Get("properties/{PropertyId}");
        AllowAnonymous();
        Group<CatalogGroup>();

        // Anonymous and unauthenticated, so the only thing standing between
        // this and an unbounded request rate is the limiter. The per-request
        // cost is already bounded elsewhere; this bounds how often. See
        // ReadRateLimitOptions for why the limit is set far looser than the
        // auth and hold policies.
        Options(x => x.RequireRateLimiting(ApiServicesRegistration.ReadRateLimitPolicy));
        Description(b => b.WithTags("Properties"));

        Summary(s =>
        {
            s.Summary = "Get a single property, with its units";
            s.Description = "Public - no authentication required.";
            s.Response<GetPropertyByIdResponse>(200, "Property returned.");
            s.Response<ProblemDetails>(404, "Property not found.");
            s.Response(429, "Too many requests.");
        });
    }

    public override async Task HandleAsync(GetPropertyByIdRequest req, CancellationToken ct)
    {
        GetPropertyByIdResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
