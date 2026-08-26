using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Reviews.Features.ListMyReviewableBookings;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Reviews;

// EndpointWithoutRequest, not Endpoint<TRequest,TResponse> - the Mediator
// request behind this has no fields at all (see ListMyReviewableBookingsRequest's
// own doc comment), and FastEndpoints' generated RequestBinder throws at
// startup for a request DTO with zero publicly-bindable properties ("Only
// request DTOs with publicly accessible properties are supported for
// request binding"). Skipping FastEndpoints' own binding for this request
// entirely sidesteps that, since there's nothing to bind from the HTTP
// request in the first place.
public class ListMyReviewableBookingsEndpoint(IMediator mediator) : EndpointWithoutRequest<ListMyReviewableBookingsResponse>
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

    public override async Task HandleAsync(CancellationToken ct)
    {
        ListMyReviewableBookingsResponse result = await mediator.Send(new ListMyReviewableBookingsRequest(), ct);
        await Send.OkAsync(result, ct);
    }
}
