using FastEndpoints;
using Mediator;
using System.Text.Json.Serialization;
namespace Availability.Features.HoldAvailability;

public record HoldAvailabilityRequest : IRequest<HoldAvailabilityResponse>
{
    public Guid UnitId { get; init; }
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public int GuestCount { get; init; }

    // Set by HoldAvailabilityEndpoint from the caller's hold-session
    // cookie, never from any binding source - [JsonIgnore] blocks the JSON
    // body (DontBind has no Body member, since body deserialization goes
    // through System.Text.Json directly), [DontBind] blocks the rest
    // (query string, route, form). HandleAsync overwriting this after
    // binding is what makes the value trustworthy for THIS endpoint -
    // these attributes keep it non-bindable for any OTHER caller sending
    // this request type through Mediator directly. See docs/adr/0016 for
    // why the token exists at all (a soft cap/ownership handle, not a
    // security boundary).
    [JsonIgnore]
    [DontBind(Source.QueryParam | Source.RouteParam | Source.FormField)]
    public string HolderToken { get; set; } = string.Empty;
}
