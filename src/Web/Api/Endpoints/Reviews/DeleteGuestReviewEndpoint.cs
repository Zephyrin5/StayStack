using BuildingBlocks.Identity;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Reviews.Features.DeleteGuestReview;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Reviews;

public class DeleteGuestReviewEndpoint(IMediator mediator) : Endpoint<DeleteGuestReviewRequest, DeleteGuestReviewResponse>
{
    public override void Configure()
    {
        Delete("guests/{GuestReviewId}");
        Policies(AuthorizationPolicies.Administrator);
        Group<ReviewsGroup>();

        Summary(s =>
        {
            s.Summary = "Delete (archive) a guest review";
            s.Description = "Soft delete - the review no longer appears in the host's guest-review list. " +
                            "Administrator only.";
            s.Response<DeleteGuestReviewResponse>(200, "Review deleted.");
            s.Response<ProblemDetails>(404, "Review not found.");
        });
    }

    public override async Task HandleAsync(DeleteGuestReviewRequest req, CancellationToken ct)
    {
        DeleteGuestReviewResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
