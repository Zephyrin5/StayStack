using BuildingBlocks.Identity;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Reviews.Features.DeleteStayReview;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Reviews;

public class DeleteStayReviewEndpoint(IMediator mediator) : Endpoint<DeleteStayReviewRequest, DeleteStayReviewResponse>
{
    public override void Configure()
    {
        Delete("stays/{StayReviewId}");
        Policies(AuthorizationPolicies.Administrator);
        Group<ReviewsGroup>();

        Summary(s =>
        {
            s.Summary = "Delete (archive) a stay review";
            s.Description = "Soft delete - the review no longer appears on the property's review list or " +
                            "rating summary. Administrator only.";
            s.Response<DeleteStayReviewResponse>(200, "Review deleted.");
            s.Response<ProblemDetails>(404, "Review not found.");
        });
    }

    public override async Task HandleAsync(DeleteStayReviewRequest req, CancellationToken ct)
    {
        DeleteStayReviewResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
