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
    ///     The guest email on the booking, required when the caller's proof of
    ///     ownership is the management link rather than an account.
    ///     <para>
    ///         Cancelling is destructive, and a link reaches anyone who saw it
    ///         - a forwarded email, a screenshot, a shared screen, a browser
    ///         history on a family laptop. Viewing is not destructive, and the
    ///         management response deliberately carries no guest email, so
    ///         someone holding only the link cannot read this value back out of
    ///         the API. That asymmetry is what makes one form field worth
    ///         anything here - it turns a leaked link from "cancel someone's
    ///         holiday" into "read their itinerary".
    ///     </para>
    ///     <para>
    ///         Not required for an authenticated customer cancelling their own
    ///         booking: a matching CustomerId is proof that cannot be
    ///         forwarded, and asking someone to retype their own address to
    ///         act on their own account is friction that buys nothing.
    ///     </para>
    /// </summary>
    [Sensitive] public string? GuestEmail { get; init; }
}
