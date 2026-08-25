using BuildingBlocks.Identity;
using Catalog.Features.UpdateUnit;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class UpdateUnitEndpoint(IMediator mediator) : Endpoint<UpdateUnitRequest, UpdateUnitResponse>
{
    public override void Configure()
    {
        Put("units/{UnitId}");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<CatalogGroup>();

        Summary(s =>
        {
            s.Summary = "Update a unit's name, occupancy, price and currency";
            s.Description = "A Host may only update units under their own property; an Administrator may " +
                            "target any. Full replacement of these fields, not a partial patch. Does not " +
                            "affect the price already locked in on any existing hold/booking - see " +
                            "HoldAvailabilityHandler's price snapshot.";
            s.Response<UpdateUnitResponse>(200, "Unit updated.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Unit not found (or belongs to a different host).");
        });
    }

    public override async Task HandleAsync(UpdateUnitRequest req, CancellationToken ct)
    {
        UpdateUnitResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
