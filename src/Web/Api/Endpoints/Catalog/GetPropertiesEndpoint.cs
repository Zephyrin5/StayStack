using Catalog.Features.GetProperties;
using FastEndpoints;
using Mediator;
namespace Api.Endpoints.Catalog;

public class GetPropertiesEndpoint(IMediator mediator) : Endpoint<GetPropertiesRequest, GetPropertiesResponse>
{
    public override void Configure()
    {
        Get("properties");
        AllowAnonymous();
        Group<CatalogGroup>();

        Summary(s =>
        {
            s.Summary = "List properties, optionally filtered by city and/or property type";
            s.Description = "Public - this is the browsing entry point, no authentication required.";
            s.Response<GetPropertiesResponse>(200, "Properties returned.");
        });
    }

    public override async Task HandleAsync(GetPropertiesRequest req, CancellationToken ct)
    {
        GetPropertiesResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
