using Catalog.Features.GetPropertyById;
using FastEndpoints;
using Mediator;
using ProblemDetails = Microsoft.AspNetCore.Mvc.ProblemDetails;

namespace Api.Endpoints.Catalog;

public class GetPropertyByIdEndpoint(IMediator mediator) : Endpoint<GetPropertyByIdRequest, GetPropertyByIdResponse>
{
    public override void Configure()
    {
        Get("properties/{PropertyId}");
        AllowAnonymous();
        Group<CatalogGroup>();
        Description(b => b.WithTags("Properties"));

        Summary(s =>
        {
            s.Summary = "Get a single property, with its units";
            s.Description = "Public - no authentication required.";
            s.Response<GetPropertyByIdResponse>(200, "Property returned.");
            s.Response<ProblemDetails>(404, "Property not found.");
        });
    }

    public override async Task HandleAsync(GetPropertyByIdRequest req, CancellationToken ct)
    {
        GetPropertyByIdResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
