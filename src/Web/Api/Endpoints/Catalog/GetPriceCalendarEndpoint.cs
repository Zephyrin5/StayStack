using Catalog.Features.GetPriceCalendar;
using FastEndpoints;
using Mediator;
namespace Api.Endpoints.Catalog;

public class GetPriceCalendarEndpoint(IMediator mediator) : Endpoint<GetPriceCalendarRequest, GetPriceCalendarResponse>
{
    public override void Configure()
    {
        Get("units/{UnitId}/price-calendar");
        AllowAnonymous();
        Group<CatalogGroup>();

        Summary(s =>
        {
            s.Summary = "Get a unit's price calendar for a date range";
            s.Description = "UnitId comes from the route, From/To from the query string. Public - no " +
                            "authentication required, this is a browsing endpoint.";
            s.Response<GetPriceCalendarResponse>(200, "Price calendar returned.");
            s.Response(400, "Validation failed.");
        });
    }

    public override async Task HandleAsync(GetPriceCalendarRequest req, CancellationToken ct)
    {
        GetPriceCalendarResponse result = await mediator.Send(req, ct);
        await Send.OkAsync(result, ct);
    }
}
