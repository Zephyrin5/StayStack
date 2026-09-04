using Api;
using FastEndpoints;
using Microsoft.AspNetCore.RateLimiting;
using Mediator;
using Reviews.Features.GetPropertyReviews;

namespace Api.Endpoints.Reviews;

public class GetPropertyReviewsEndpoint(IMediator mediator) : Endpoint<GetPropertyReviewsRequest, GetPropertyReviewsResponse>
{
    public override void Configure()
    {
        Get("stays/property/{PropertyId}");
        AllowAnonymous();
        Group<ReviewsGroup>();

        // Anonymous and unauthenticated, so the only thing standing between
        // this and an unbounded request rate is the limiter. The per-request
        // cost is already bounded elsewhere; this bounds how often. See
        // ReadRateLimitOptions for why the limit is set far looser than the
        // auth and hold policies.
        Options(x => x.RequireRateLimiting(ApiServicesRegistration.ReadRateLimitPolicy));

        Summary(s =>
        {
            s.Summary = "List a property's stay reviews and rating summary";
            s.Description = "Public. RatingSummary is computed across every review for the property, not " +
                            "just the current page - zeroed (not omitted) when Count is 0. Most recent " +
                            "review first, paginated (defaults to page 1, 20 per page).";
            s.Response<GetPropertyReviewsResponse>(200, "Reviews returned.");
        });
    }

    public override async Task HandleAsync(GetPropertyReviewsRequest req, CancellationToken ct)
    {
        GetPropertyReviewsResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
