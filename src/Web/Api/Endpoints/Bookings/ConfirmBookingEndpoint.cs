using Bookings.Features.ConfirmBooking;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Bookings;

public class ConfirmBookingEndpoint(IMediator mediator) : Endpoint<ConfirmBookingRequest, ConfirmBookingResponse>
{
    public override void Configure()
    {
        Post("");
        AllowAnonymous();
        Group<BookingsGroup>();

        // Anonymous and a real DB write with financial consequences (see
        // docs/adr/0016) - same "auth" policy as CancelBookingEndpoint/
        // GetBookingForManagementEndpoint/InitiateTransactionEndpoint, the
        // established rate limit for this exact class of sensitive,
        // guest-checkout-capable Bookings endpoint.
        Options(x => x.RequireRateLimiting(ApiServicesRegistration.AuthRateLimitPolicy));

        Summary(s =>
        {
            s.Summary = "Confirm a held unit into a booking";
            s.Description = "Public - guest checkout is supported, no account required. If the caller is " +
                            "authenticated, the booking's CustomerId is set from their token automatically; " +
                            "guest name/email/phone are always stored on the booking either way. Created as " +
                            "Pending - payment integration isn't built yet, so nothing confirms a booking today.";
            s.Response<ConfirmBookingResponse>(200, "Booking created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Hold not found, already used, or expired.");
            s.Response<ProblemDetails>(409,
                "Another confirmation for this hold is in flight, or this one was interrupted and rolled back. " +
                "Retryable - though an interrupted confirmation releases its hold, so the guest may need to re-hold.");
            s.Response(429, "Too many requests.");
        });
    }

    public override async Task HandleAsync(ConfirmBookingRequest req, CancellationToken ct)
    {
        ConfirmBookingResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
