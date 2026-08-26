using Bookings.Features.GetBookingForManagement;
using FastEndpoints;
using Mediator;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Bookings;

public class GetBookingForManagementEndpoint(IMediator mediator)
    : Endpoint<GetBookingForManagementRequest, GetBookingForManagementResponse>
{
    public override void Configure()
    {
        Get("{BookingId}/manage");
        AllowAnonymous();
        Group<BookingsGroup>();

        Summary(s =>
        {
            s.Summary = "View a booking for self-service management";
            s.Description = "Public - same two-path ownership proof as POST /bookings/{id}/cancel " +
                            "(authenticated CustomerId, or a guest-checkout ManagementToken via the " +
                            "?managementToken= query string). Backs the guest-checkout \"manage your " +
                            "booking\" page: shows whether the booking can still be cancelled, and whether " +
                            "it's eligible for a review (Confirmed and checkout has passed) - a review " +
                            "already existing isn't checked here, since Bookings has no dependency on " +
                            "Reviews; the client asks Reviews separately when it loads the review form.";
            s.Response<GetBookingForManagementResponse>(200, "Booking returned.");
            s.Response<ProblemDetails>(404, "Booking not found, belongs to someone else, or the token is missing/wrong.");
        });
    }

    public override async Task HandleAsync(GetBookingForManagementRequest req, CancellationToken ct)
    {
        GetBookingForManagementResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
