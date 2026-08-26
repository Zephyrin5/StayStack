using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Reviews.Features.ListMyReviewableBookings;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Reviews;

public class ListMyReviewableBookingsEndpoint(IMediator mediator)
    : Endpoint<ListMyReviewableBookingsRequest, ListMyReviewableBookingsResponse>
{
    public override void Configure()
    {
        Get("stays/mine");
        Group<ReviewsGroup>();

        Summary(s =>
        {
            s.Summary = "List the caller's own bookings that are ready to review";
            s.Description = "Requires authentication - guest-checkout bookings surface as reviewable " +
                            "through GET /bookings/{id}/manage instead, since there's no account to list " +
                            "them against. Confirmed, checkout-passed, not-yet-reviewed bookings only. Unpaged.";
            s.Response<ListMyReviewableBookingsResponse>(200, "Bookings returned.");
            s.Response<ProblemDetails>(401, "Not authenticated.");
        });
    }

    public override async Task HandleAsync(ListMyReviewableBookingsRequest req, CancellationToken ct)
    {
        ListMyReviewableBookingsResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
