using SeedWork.ValueObjects;
namespace Bookings.Contracts;

/// <summary>
///     Lets Transactions resolve a booking's amount/currency without ever
///     referencing Bookings' own entities or AppBookingsDbContext directly -
///     same boundary reasoning as Catalog.Contracts.IUnitLookup.
/// </summary>
public interface IBookingLookup
{
    Task<BookingSummary?> GetBookingAsync(Guid bookingId, CancellationToken cancellationToken);

    /// <summary>
    ///     Lets Reviews authorize a review submission without ever
    ///     referencing Booking or AppBookingsDbContext directly - same
    ///     ownership proof CancelBookingHandler itself uses (a matching
    ///     customerId, or a matching guest-checkout management token), via
    ///     the same internal BookingAccessChecker both go through. Null if
    ///     the booking doesn't exist or the caller doesn't own it - doesn't
    ///     distinguish the two, same "doesn't exist and isn't yours must
    ///     look identical" reasoning as everywhere else this pattern is used.
    /// </summary>
    Task<BookingAccessResult?> VerifyBookingAccessAsync(
        Guid bookingId, Guid? customerId, string? managementToken, CancellationToken cancellationToken);

    /// <summary>
    ///     Every Confirmed booking belonging to this customer - what
    ///     ListMyReviewableBookingsHandler (Reviews) filters by
    ///     checkout-passed and not-yet-reviewed, since Reviews has no
    ///     notion of Booking/CustomerId itself. Same "give me every X
    ///     owned by Y" shape as IUnitLookup.GetUnitIdsForHostAsync.
    ///     Unfiltered by checkout date - the caller decides what "past"
    ///     means for its own purposes.
    /// </summary>
    Task<IReadOnlyList<BookingAccessResult>> GetConfirmedBookingsForCustomerAsync(
        Guid customerId, CancellationToken cancellationToken);

    /// <summary>
    ///     A raw lookup, no ownership check - what CreateGuestReviewHandler
    ///     (Reviews) uses, since a host reviewing a guest is authorized by
    ///     owning the booking's unit (via Catalog.Contracts.IUnitLookup),
    ///     not by a customerId/managementToken match the way
    ///     VerifyBookingAccessAsync's two paths are. Null if the booking
    ///     doesn't exist.
    /// </summary>
    Task<BookingAccessResult?> GetBookingDetailsAsync(Guid bookingId, CancellationToken cancellationToken);
}

public record BookingSummary
{
    public Guid Id { get; init; }
    public Money TotalPrice { get; init; }

    // True only while Pending - the one state a transaction can actually
    // be initiated from. Exposed as a bool rather than the real
    // BookingStatus enum since that type lives in Bookings.Entities, not
    // this dependency-free contract, and callers only ever need this one
    // fact about it.
    public bool IsPending { get; init; }
}

public record BookingAccessResult
{
    public Guid BookingId { get; init; }
    public Guid UnitId { get; init; }
    public DateOnly CheckIn { get; init; }
    public DateOnly CheckOut { get; init; }

    // Same bool-not-enum reasoning as BookingSummary.IsPending - Reviews
    // only ever needs this one fact about BookingStatus.
    public bool IsConfirmed { get; init; }
    public required string GuestEmail { get; init; }
    public Guid? CustomerId { get; init; }

    // The booking's own snapshotted zone, so callers resolve "today" per
    // booking rather than once per request - what makes a list spanning
    // several properties correct (docs/adr/0018).
    public required string TimeZoneId { get; init; }
}
