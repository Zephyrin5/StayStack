using BuildingBlocks.Identity;
using Catalog.Features.CreateProperty;
using FastEndpoints;
using Mediator;
using Microsoft.AspNetCore.Mvc;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class CreatePropertyEndpoint(IMediator mediator) : Endpoint<CreatePropertyRequest, CreatePropertyResponse>
{
    public override void Configure()
    {
        Post("properties");
        Policies(AuthorizationPolicies.Host);
        Group<CatalogGroup>();
        Description(b => b.WithTags("Properties"));

        Summary(s =>
        {
            s.Summary = "Create a new property under the caller's own host";
            s.Description = "HostId is derived from the caller's token, never accepted as input - see " +
                            "AdminCreatePropertyEndpoint for the route that lets an Administrator create a " +
                            "property under an arbitrary host.";
            s.Response<CreatePropertyResponse>(200, "Property created.");
            s.Response<ValidationProblemDetails>(400, "Validation failed.");
            s.Response<ProblemDetails>(403, "Caller is not linked to a host.");
        });
    }

    public override async Task HandleAsync(CreatePropertyRequest req, CancellationToken ct)
    {
        CreatePropertyResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
