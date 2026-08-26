using FastEndpoints;
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
