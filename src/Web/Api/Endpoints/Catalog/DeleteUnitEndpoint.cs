using BuildingBlocks.Identity;
using Catalog.Features.DeleteUnit;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class DeleteUnitEndpoint(IMediator mediator) : Endpoint<DeleteUnitRequest, DeleteUnitResponse>
{
    public override void Configure()
    {
        Delete("units/{UnitId}");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<CatalogGroup>();

        Summary(s =>
        {
            s.Summary = "Delete (archive) a unit";
            s.Description = "Soft delete - stops the unit from being listed/searched/added to going " +
                            "forward. Existing holds/bookings against it are left untouched. A Host may " +
                            "only delete units under their own property; an Administrator may target any.";
            s.Response<DeleteUnitResponse>(200, "Unit deleted.");
            s.Response<ProblemDetails>(404, "Unit not found (or belongs to a different host).");
        });
    }

    public override async Task HandleAsync(DeleteUnitRequest req, CancellationToken ct)
    {
        DeleteUnitResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
