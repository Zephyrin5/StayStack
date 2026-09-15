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
    ///     CancelBookingHandler enforces and GetBookingForManagement reports.
    ///     <para>
    ///         Sent rather than derived by the client: the rule needs the
    ///         booking's own time zone, which a client cannot reliably apply, and
    ///         a rule with one implementation cannot drift from the API.
    ///     </para>
    /// </summary>
    public bool CanCancel { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}
