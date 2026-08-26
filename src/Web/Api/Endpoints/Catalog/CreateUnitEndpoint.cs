using BuildingBlocks.Identity;
using Catalog.Features.CreateUnit;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class CreateUnitEndpoint(IMediator mediator) : Endpoint<CreateUnitRequest, CreateUnitResponse>
{
    public override void Configure()
    {
        Post("units");
        Policies(AuthorizationPolicies.HostOrAdministrator);
        Group<CatalogGroup>();
        Description(b => b.WithTags("Units"));

        Summary(s =>
        {
            s.Summary = "Create a new unit under a property";
            s.Description = "A Host may only create units under their own property; an Administrator may " +
                            "target any property.";
            s.Response<CreateUnitResponse>(200, "Unit created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(404, "Property not found (or belongs to a different host).");
        });
    }

    public override async Task HandleAsync(CreateUnitRequest req, CancellationToken ct)
    {
        CreateUnitResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
