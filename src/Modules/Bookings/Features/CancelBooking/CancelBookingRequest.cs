using BuildingBlocks.Observability;
using Mediator;
namespace Bookings.Features.CancelBooking;

public record CancelBookingRequest : IRequest<CancelBookingResponse>
{
    public Guid BookingId { get; init; }

    // Only for a guest-checkout booking - see BookingAccessChecker. Never
    // read for an authenticated caller, whose CustomerId already proves
    // ownership.

    /// <summary>
    ///     The guest email on the booking, required when the caller's proof of ownership is the
    ///     management link rather than an account.
    ///     <para>
    ///         A link reaches anyone who saw it, and cancelling is destructive. The management
    ///         response carries no guest email, so a leaked link cannot read this value back out of
    ///         the API - which turns it from "cancel someone's holiday" into "read their itinerary".
    ///         An authenticated customer needs no such field: a matching CustomerId cannot be forwarded.
    ///     </para>
    /// </summary>
    [Sensitive] public string? GuestEmail { get; init; }
}
