using BuildingBlocks.Identity;
using BuildingBlocks.Pagination;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Reviews.Features.GetHostStayReviews;
using Reviews.Features.GetPropertyReviews;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Reviews;

public class GetHostStayReviewsEndpoint(IMediator mediator)
    : Endpoint<GetHostStayReviewsRequest, PagedResponse<StayReviewSummary>>
{
    public override void Configure()
    {
        Get("stays/host");
        Policies(AuthorizationPolicies.Host);
        Group<ReviewsGroup>();

        Summary(s =>
        {
            s.Summary = "List reviews about the caller's own properties";
            s.Description = "Requires the caller to be a host - HostId is derived from the caller's token, " +
                            "never accepted as input. Lets a host read and decide whether to reply. Most " +
                            "recent first, paginated (defaults to page 1, 20 per page).";
            s.Response<PagedResponse<StayReviewSummary>>(200, "Reviews returned.");
            s.Response<ProblemDetails>(403, "Caller is not linked to a host.");
        });
    }

    public override async Task HandleAsync(GetHostStayReviewsRequest req, CancellationToken ct)
    {
        PagedResponse<StayReviewSummary> result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
