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

    // Set by HoldAvailabilityEndpoint from the connection's peer address
    // (Api.Security.ClientNetworkKey), never by the caller. [JsonIgnore]
    // keeps it out of the body (DontBind has no Body member, since body
    // deserialization goes through System.Text.Json directly) and [DontBind]
    // blocks the rest; HandleAsync then assigns it unconditionally after
    // binding, which is what makes the value trustworthy for this endpoint
    // while keeping it non-bindable for any other caller sending this
    // request type through Mediator directly.
    //
    // It matters more here than it did for the holder_token that used to sit
    // beside it: this one IS a security control - it is what the
    // concurrent-hold cap counts by - so a caller who could set it from the
    // body would be back to choosing their own budget, which is exactly the
    // defect that moved the cap off the cookie in the first place.
    [JsonIgnore]
    [DontBind(Source.QueryParam | Source.RouteParam | Source.FormField)]
    public string ClientKey { get; set; } = string.Empty;
}
