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
    ///     The client's retry key, from the <c>Idempotency-Key</c> header. Optional, but a client that
    ///     takes checkout seriously always sends one.
    ///     <para>
    ///         Header rather than body keeps it out of the request fingerprint by construction: the
    ///         fingerprint identifies the checkout, and a value that differs per attempt cannot be
    ///         part of it. Assigned by the endpoint after binding and bindable from nowhere else, so a
    ///         caller cannot contradict the header through a second channel.
    ///     </para>
    ///     <para>
    ///         Its length rule is therefore in ConfirmBookingHandler, not the validator: validators
    ///         run before the endpoint body, so a rule there would pass on every request.
    ///     </para>
    /// </summary>
    [JsonIgnore]
    [DontBind(Source.QueryParam | Source.RouteParam | Source.FormField)]
    [Sensitive] public string? IdempotencyKey { get; set; }
}
