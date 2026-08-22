using Mediator;
namespace Bookings.Features.ConfirmBooking;

public record ConfirmBookingRequest : IRequest<ConfirmBookingResponse>
{
    public Guid HoldId { get; init; }
    public required string GuestName { get; init; }
    public required string GuestEmail { get; init; }
    public string? GuestPhone { get; init; }

    // Re-collected here rather than read back off the originating Hold -
    // UnitAvailabilityHold (Catalog) only ever validated guest count
    // against the unit's max occupancy at hold time, it never persisted
    // it (see HoldConfirmation's own note). Simpler to ask again here than
    // to add a column to Catalog's already-published schema for this.
    public int GuestCount { get; init; }
}
