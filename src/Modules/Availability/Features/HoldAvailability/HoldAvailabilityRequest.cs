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

    // Set by HoldAvailabilityEndpoint from the caller's hold-session cookie,
    // never from any FastEndpoints binding source - [JsonIgnore] blocks the
    // JSON body (the one source Source below can't cover - FastEndpoints'
    // own DontBind has no Body member, since body deserialization goes
    // through System.Text.Json directly), [DontBind] blocks the rest
    // (query string, route, form) that a POST could still theoretically
    // supply it from. HandleAsync overwriting this after binding is still
    // what actually makes the value trustworthy for THIS endpoint - these
    // attributes are what keep it non-bindable for any *other* caller that
    // ever sends this same request type through Mediator directly, request
    // body included, without going through that endpoint at all. See
    // docs/adr/0016 for why the token exists in the first place (a soft
    // cap/ownership handle, not a security boundary).
    [JsonIgnore]
    [DontBind(Source.QueryParam | Source.RouteParam | Source.FormField)]
    public string HolderToken { get; set; } = string.Empty;
}
