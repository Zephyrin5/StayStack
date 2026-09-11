using FastEndpoints;
using BuildingBlocks.Observability;
using Mediator;
using System.Text.Json.Serialization;
namespace Bookings.Features.ConfirmBooking;

public record ConfirmBookingRequest : IRequest<ConfirmBookingResponse>
{
    public Guid HoldId { get; init; }
    [Sensitive] public required string GuestName { get; init; }
    [Sensitive] public required string GuestEmail { get; init; }
    [Sensitive] public string? GuestPhone { get; init; }

    // Optional - see ConfirmBookingHandler for how a redeemed code is
    // exclusive of the length-of-stay discount rather than stacking with
    // it. A rejection surfaces as a field-keyed ValidationException so the
    // client can show the specific reason against this field.
    [Sensitive] public string? PromoCode { get; init; }

    /// <summary>
    ///     The client's retry key, taken from the <c>Idempotency-Key</c>
    ///     request header. Optional - omitting it gives exactly the previous
    ///     behaviour, so no existing caller breaks - but a client that takes
    ///     payment or checkout seriously should always send one.
    ///     <para>
    ///         Header rather than body, following the usual convention: it is
    ///         metadata about the delivery of the request, not part of what is
    ///         being asked for. Keeping it out of the body also keeps it out
    ///         of the request fingerprint by construction, which matters -
    ///         the fingerprint has to identify the checkout, and a value that
    ///         differs on every attempt cannot be part of it.
    ///     </para>
    ///     <para>
    ///         Not bindable from body, query, route or form, and assigned
    ///         unconditionally by the endpoint after binding - the same shape
    ///         as HoldAvailabilityRequest.ClientKey, for the same reason. A
    ///         caller who could set it through a second channel could
    ///         contradict the header, and nothing here could tell which one
    ///         the retry meant.
    ///     </para>
    ///     <para>
    ///         Its length rule therefore lives in ConfirmBookingHandler, not
    ///         in ConfirmBookingRequestValidator. Validators run against the
    ///         bound request before the endpoint body executes, so a value the
    ///         endpoint assigns afterwards is invisible to them - a rule put
    ///         there would pass on every request, including the ones it exists
    ///         to reject.
    ///     </para>
    /// </summary>
    [JsonIgnore]
    [DontBind(Source.QueryParam | Source.RouteParam | Source.FormField)]
    [Sensitive] public string? IdempotencyKey { get; set; }
}
