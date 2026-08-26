using SeedWork.Enums;
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
}

public record BookingSummary
{
    public Guid Id { get; init; }
    public decimal TotalPrice { get; init; }
    public Currency Currency { get; init; }

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
    public DateOnly CheckOut { get; init; }

    // Same bool-not-enum reasoning as BookingSummary.IsPending - Reviews
    // only ever needs this one fact about BookingStatus.
    public bool IsConfirmed { get; init; }
    public required string GuestEmail { get; init; }
    public Guid? CustomerId { get; init; }
}
