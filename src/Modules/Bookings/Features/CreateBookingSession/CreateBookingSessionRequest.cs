using BuildingBlocks.Observability;
using Mediator;
namespace Bookings.Features.CreateBookingSession;

/// <summary>
///     Exchanges a long-lived booking-management token for a short-lived
///     session. The only request in the system that carries the management
///     token, and the reason every other one no longer has to.
/// </summary>
public record CreateBookingSessionRequest : IRequest<CreateBookingSessionResponse>
{
    public Guid BookingId { get; init; }

    /// <summary>
    ///     In the body, never the query string - this is the whole point of
    ///     the exchange. A query string reaches the access log of every hop,
    ///     the browser's history, and the Referer header of any outbound link
    ///     on the page that follows.
    /// </summary>
    [Sensitive] public required string ManagementToken { get; init; }
}
