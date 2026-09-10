using Bookings.Entities;
using SeedWork.Enums;
namespace Bookings.Features.GetMyBookings;

public record BookingSummary
{
    public Guid BookingId { get; init; }
    public Guid UnitId { get; init; }
    public Dictionary<string, string> UnitName { get; init; } = new Dictionary<string, string>();
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }
    public int GuestCount { get; init; }
    public decimal TotalPrice { get; init; }
    public Currency Currency { get; init; }
    public BookingStatus BookingStatus { get; init; }

    /// <summary>
    ///     Whether the customer can still cancel this booking themselves,
    ///     from <c>Booking.CanBeCancelledOn</c> - the same rule
    ///     CancelBookingHandler enforces and GetBookingForManagement already
    ///     reported.
    ///     <para>
    ///         Sent rather than left for the client to derive. The client's
    ///         bookings list was deriving it as "not cancelled", which is
    ///         what the server itself used to do; when cancellation became
    ///         bounded by check-in, the guest-checkout view corrected itself
    ///         because it reads this kind of flag from the API, while the
    ///         signed-in list went on offering an action that now answers
    ///         409. A rule with one implementation cannot drift that way, and
    ///         this one already needs the booking's own time zone to
    ///         evaluate - something a client has no reliable way to apply.
    ///     </para>
    /// </summary>
    public bool CanCancel { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
