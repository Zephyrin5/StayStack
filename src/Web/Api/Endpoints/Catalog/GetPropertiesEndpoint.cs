using BuildingBlocks.Pagination;
using Catalog.Features.GetProperties;
using FastEndpoints;
using Mediator;
namespace Api.Endpoints.Catalog;

public class GetPropertiesEndpoint(IMediator mediator) : Endpoint<GetPropertiesRequest, PagedResponse<PropertySummary>>
{
    public override void Configure()
    {
        Get("properties");
        AllowAnonymous();
        Group<CatalogGroup>();

        Summary(s =>
        {
            s.Summary = "List properties, optionally filtered by city, property type, date range and/or guest count";
            s.Description = "Public - this is the browsing entry point, no authentication required. " +
                            "CheckIn/CheckOut must be provided together; a property matches only if it has " +
                            "at least one unit that satisfies both the guest count and the date range. " +
                            "Paginated - defaults to page 1, 20 per page.";
            s.Response<PagedResponse<PropertySummary>>(200, "Properties returned.");
        });
    }

    public override async Task HandleAsync(GetPropertiesRequest req, CancellationToken ct)
    {
        PagedResponse<PropertySummary> result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
