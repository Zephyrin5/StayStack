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

        // Anonymous and a real write: the same limit as the other guest-checkout endpoints (docs/adr/0016).
        Options(x => x.RequireRateLimiting(ApiServicesRegistration.AuthRateLimitPolicy));

        Summary(s =>
        {
            s.Summary = "Confirm a held unit into a booking";
            s.Description = "Public - guest checkout is supported, no account required. If the caller is " +
                            "authenticated, the booking's CustomerId is set from their token automatically; " +
                            "guest name/email/phone are always stored on the booking either way. Created as " +
                            "Pending - payment integration isn't built yet, so nothing confirms a booking today.";
            // The same booking with a freshly issued token, not a byte-identical replay.
            s.Description += " Send an `Idempotency-Key` header (16-128 characters, a UUID is ideal) to make " +
                             "retries safe: if the connection drops after the booking commits, replaying the same " +
                             "key and the same body returns that booking's current state together with a **newly " +
                             "issued** management token - the original is never stored, only its hash, so it " +
                             "cannot be handed back. The first token stays valid; the replay adds one rather than " +
                             "rotating. Replayable for 24 hours.";
            s.Response<ConfirmBookingResponse>(200, "Booking created, or the booking behind this key with a fresh management token.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Hold not found, already used, or expired.");
            s.Response<ProblemDetails>(409,
                "An Idempotency-Key was replayed with a different request body, or its replay window has passed.");
            s.Response(429, "Too many requests.");
        });
    }

    public override async Task HandleAsync(ConfirmBookingRequest req, CancellationToken ct)
    {
        // Assigned unconditionally, overwriting whatever bound; null when the header is absent.
        req.IdempotencyKey = HttpContext.Request.Headers["Idempotency-Key"].FirstOrDefault();

        ConfirmBookingResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
