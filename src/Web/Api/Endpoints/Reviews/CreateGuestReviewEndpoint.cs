using BuildingBlocks.Identity;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Reviews.Features.CreateGuestReview;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Reviews;

public class CreateGuestReviewEndpoint(IMediator mediator) : Endpoint<CreateGuestReviewRequest, CreateGuestReviewResponse>
{
    public override void Configure()
    {
        Post("guests");
        Policies(AuthorizationPolicies.Host);
        Group<ReviewsGroup>();

        Summary(s =>
        {
            s.Summary = "Leave a private review of a guest";
            s.Description = "Requires the caller to be the host on the booking's underlying unit - a booking " +
                            "belonging to a different host's property returns 404, same as any other " +
                            "ownership mismatch in this API. The booking must be Confirmed and checkout must " +
                            "have passed. One review per booking - a second attempt returns 409. Visible only " +
                            "to hosts, never surfaced to the guest.";
            s.Response<CreateGuestReviewResponse>(200, "Review created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed, booking not yet confirmed, or checkout hasn't passed.");
            s.Response<ProblemDetails>(404, "Booking not found, or belongs to a different host's property.");
            s.Response<ProblemDetails>(409, "This booking's guest has already been reviewed.");
        });
    }

    public override async Task HandleAsync(CreateGuestReviewRequest req, CancellationToken ct)
    {
        CreateGuestReviewResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
