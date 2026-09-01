using Api.Security;
using Availability.Features.HoldAvailability;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
namespace Api.Endpoints.Availability;

public class HoldAvailabilityEndpoint(
    IMediator mediator, IOptions<CookieSecurityOptions> cookieSecurity, TimeProvider timeProvider)
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
            s.Description = "Public - holding a room is a pre-checkout action that must work for guests, not " +
                            "just signed-in customers. Holds expire after 15 minutes if never confirmed into " +
                            "a booking.";
            s.Response<HoldAvailabilityResponse>(200, "Hold created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response(404, "Unit not found.");
            s.Response(409, "Unit is unavailable for some or all of the requested range.");
            s.Response(429, "Too many concurrent holds from this network, or too many requests.");
        });
    }

    public override async Task HandleAsync(HoldAvailabilityRequest req, CancellationToken ct)
    {
        req.HolderToken = HttpContext.Request.GetOrCreateHoldSessionToken(HttpContext.Response, cookieSecurity.Value, timeProvider);

        // Assigned unconditionally, overwriting whatever bound - see the
        // property's own comment. Correct only once ForwardedHeaders is
        // processing a real proxy's headers, same caveat the "holds"
        // rate-limit partition already carries.
        req.ClientKey = ClientNetworkKey.Resolve(HttpContext.Connection.RemoteIpAddress);

        HoldAvailabilityResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
