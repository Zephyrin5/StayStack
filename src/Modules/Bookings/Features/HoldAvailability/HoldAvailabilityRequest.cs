using BuildingBlocks.Observability;
using FastEndpoints;
using Mediator;
using System.Text.Json.Serialization;
namespace Bookings.Features.HoldAvailability;

public record HoldAvailabilityRequest : IRequest<HoldAvailabilityResponse>
{
    public Guid UnitId { get; init; }
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public int GuestCount { get; init; }

    // Set by the endpoint from the connection's peer address, never by the caller: [JsonIgnore] keeps
    // it out of the body and [DontBind] blocks every other source. A security control - the hold cap
    // counts by it, so a caller who could set it would choose their own budget.
    //
    // [Sensitive] covers the response side, which those two attributes say nothing about:
    // PayloadRedactor is a deny-list, so an unmarked property is written verbatim into traces and
    // logs, and this is the caller's approximate location.
    [JsonIgnore]
    [DontBind(Source.QueryParam | Source.RouteParam | Source.FormField)]
    [Sensitive]
    public string ClientKey { get; set; } = string.Empty;
}
