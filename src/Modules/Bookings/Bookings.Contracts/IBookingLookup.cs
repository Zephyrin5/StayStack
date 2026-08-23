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
