using BuildingBlocks.Identity;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Reviews.Features.ReplyToStayReview;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Reviews;

public class ReplyToStayReviewEndpoint(IMediator mediator) : Endpoint<ReplyToStayReviewRequest, ReplyToStayReviewResponse>
{
    public override void Configure()
    {
        Post("stays/{StayReviewId}/reply");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<ReviewsGroup>();

        Summary(s =>
        {
            s.Summary = "Post a host reply to a stay review";
            s.Description = "One reply per review - a second attempt returns 409. A Host may only reply to " +
                            "reviews about their own properties; an Administrator may target any.";
            s.Response<ReplyToStayReviewResponse>(200, "Reply posted.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Review not found (or belongs to a different host).");
            s.Response<ProblemDetails>(409, "This review already has a reply.");
        });
    }

    public override async Task HandleAsync(ReplyToStayReviewRequest req, CancellationToken ct)
    {
        ReplyToStayReviewResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
