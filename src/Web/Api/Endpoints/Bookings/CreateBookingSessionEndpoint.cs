using Bookings.Features.CreateBookingSession;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.RateLimiting;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Bookings;

public class CreateBookingSessionEndpoint(IMediator mediator)
    : Endpoint<CreateBookingSessionRequest, CreateBookingSessionResponse>
{
    public override void Configure()
    {
        Post("{BookingId}/manage/session");
        AllowAnonymous();
        Group<BookingsGroup>();

        // Same "auth" policy as the other guest-checkout-capable endpoints
        // (docs/adr/0016). It matters more here than on any of them: this is
        // the one endpoint that takes the long-lived management token, so it
        // is the one an attacker would grind against. The token is 64 random
        // bytes, so guessing is not the threat - but rate limiting is what
        // keeps a leaked-and-revoked token from being probed at speed, and
        // what bounds the cost of the hash comparison.
        Options(x => x.RequireRateLimiting(ApiServicesRegistration.AuthRateLimitPolicy));

        Summary(s =>
        {
            s.Summary = "Exchange a booking-management token for a short-lived session";
            s.Description =
                "Public - guest checkout is supported, no account required. Takes the long-lived management " +
                "token in the **body** and returns a short-lived session token to send as " +
                "`Authorization: Bearer <sessionToken>` on the management endpoints (view, cancel, review, " +
                "start payment). The management token itself then never travels again until the session " +
                "expires, at which point the same link can be exchanged for a new one - it is not rotated or " +
                "consumed by this call. " +
                "A session is scoped to one booking and cannot mint another session.";
            s.Response<CreateBookingSessionResponse>(200, "Session created.");
            s.Response<ProblemDetails>(404,
                "No such booking, the token doesn't match it, or the token's window has closed - not " +
                "distinguished, deliberately.");
            s.Response(429, "Too many requests.");
        });
    }

    public override async Task HandleAsync(CreateBookingSessionRequest req, CancellationToken ct)
    {
        CreateBookingSessionResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
