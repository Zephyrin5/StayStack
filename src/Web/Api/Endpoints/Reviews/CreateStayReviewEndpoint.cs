using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Reviews.Features.CreateStayReview;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Reviews;

public class CreateStayReviewEndpoint(IMediator mediator) : Endpoint<CreateStayReviewRequest, CreateStayReviewResponse>
{
    public override void Configure()
    {
        Post("stays");
        AllowAnonymous();
        Group<ReviewsGroup>();

        Summary(s =>
        {
            s.Summary = "Leave a review for a completed stay";
            s.Description = "Public - same two-path ownership proof as POST /bookings/{id}/cancel: an " +
                            "authenticated caller whose CustomerId matches the booking, or a guest-checkout " +
                            "caller supplying the ManagementToken returned once at confirm time. The booking " +
                            "must be Confirmed and checkout must have passed. One review per booking - a " +
                            "second attempt returns 409.";
            s.Response<CreateStayReviewResponse>(200, "Review created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed, booking not yet confirmed, or checkout hasn't passed.");
            s.Response<ProblemDetails>(404, "Booking not found, belongs to someone else, or the token is missing/wrong.");
            s.Response<ProblemDetails>(409, "This booking has already been reviewed.");
        });
    }

    public override async Task HandleAsync(CreateStayReviewRequest req, CancellationToken ct)
    {
        CreateStayReviewResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
