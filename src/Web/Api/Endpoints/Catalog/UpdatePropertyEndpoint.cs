using BuildingBlocks.Identity;
using Catalog.Features.UpdateProperty;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class UpdatePropertyEndpoint(IMediator mediator) : Endpoint<UpdatePropertyRequest, UpdatePropertyResponse>
{
    public override void Configure()
    {
        Put("properties/{PropertyId}");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<CatalogGroup>();

        Summary(s =>
        {
            s.Summary = "Update a property's name, type and city";
            s.Description = "A Host may only update their own property; an Administrator may target any " +
                            "property. Full replacement of these fields, not a partial patch.";
            s.Response<UpdatePropertyResponse>(200, "Property updated.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Property not found (or belongs to a different host).");
        });
    }

    public override async Task HandleAsync(UpdatePropertyRequest req, CancellationToken ct)
    {
        UpdatePropertyResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
