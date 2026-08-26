using BuildingBlocks.Identity;
using Catalog.Features.DeleteProperty;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class DeletePropertyEndpoint(IMediator mediator) : Endpoint<DeletePropertyRequest, DeletePropertyResponse>
{
    public override void Configure()
    {
        Delete("properties/{PropertyId}");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<CatalogGroup>();
        Description(b => b.WithTags("Properties"));

        Summary(s =>
        {
            s.Summary = "Delete (archive) a property and all of its units";
            s.Description = "Soft delete - the property and every one of its units are archived together, " +
                            "so neither is reachable through any listing/search/lookup afterward. Existing " +
                            "holds and bookings against those units are left untouched; this only stops new " +
                            "ones. A Host may only delete their own property; an Administrator may target any.";
            s.Response<DeletePropertyResponse>(200, "Property deleted.");
            s.Response<ProblemDetails>(404, "Property not found (or belongs to a different host).");
        });
    }

    public override async Task HandleAsync(DeletePropertyRequest req, CancellationToken ct)
    {
        DeletePropertyResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
