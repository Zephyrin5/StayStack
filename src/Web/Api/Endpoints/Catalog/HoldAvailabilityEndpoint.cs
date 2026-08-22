using Catalog.Features.HoldAvailability;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
namespace Api.Endpoints.Catalog;

public class HoldAvailabilityEndpoint(IMediator mediator) : Endpoint<HoldAvailabilityRequest, HoldAvailabilityResponse>
{
    public override void Configure()
    {
        Post("holds");
        AllowAnonymous();
        Group<CatalogGroup>();

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
        });
    }

    public override async Task HandleAsync(HoldAvailabilityRequest req, CancellationToken ct)
    {
        HoldAvailabilityResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
