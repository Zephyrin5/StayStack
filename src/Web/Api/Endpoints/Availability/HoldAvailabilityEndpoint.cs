using Api.Security;
using Bookings.Features.HoldAvailability;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
namespace Api.Endpoints.Availability;

// Still under Endpoints/Availability, and still on /api/availability/holds,
// though the module behind it merged into Bookings. This folder mirrors the
// public API surface rather than the module layout, and the merge is an
// internal reorganisation that clients should not be able to observe.
public class HoldAvailabilityEndpoint(IMediator mediator)
    : Endpoint<HoldAvailabilityRequest, HoldAvailabilityResponse>
{
    public override void Configure()
    {
        Post("holds");
        AllowAnonymous();
        Group<AvailabilityGroup>();
        Options(x => x.RequireRateLimiting(ApiServicesRegistration.HoldRateLimitPolicy));

        Summary(s =>
        {
            s.Summary = "Hold a unit for a stay range, ahead of completing a booking";
            // The window is configuration (HoldCapOptions.HoldWindowMinutes), so the response says what
            // this deployment does rather than what the default is.
            s.Description = "Public - holding a room is a pre-checkout action that must work for guests, not " +
                            "just signed-in customers. A hold expires if it is never confirmed into a booking; " +
                            "the response carries the instant it expires.";
            s.Response<HoldAvailabilityResponse>(200, "Hold created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response(404, "Unit not found.");
            s.Response(409, "Unit is unavailable for some or all of the requested range.");
            s.Response(429, "Too many concurrent holds from this network, or too many requests.");
        });
    }

    public override async Task HandleAsync(HoldAvailabilityRequest req, CancellationToken ct)
    {
        // Assigned unconditionally, overwriting whatever bound - see the
        // property's own comment. Correct only once ForwardedHeaders is
        // processing a real proxy's headers, same caveat the "holds"
        // rate-limit partition already carries.
        req.ClientKey = ClientNetworkKey.Resolve(HttpContext.Connection.RemoteIpAddress);

        HoldAvailabilityResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
