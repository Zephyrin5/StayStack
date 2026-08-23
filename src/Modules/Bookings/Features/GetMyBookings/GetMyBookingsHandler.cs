using Bookings.Entities;
using BuildingBlocks.Identity;
using Catalog.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Bookings.Features.GetMyBookings;

public class GetMyBookingsHandler(
    AppBookingsDbContext dbContext,
    IUnitLookup unitLookup,
    ICurrentUserProvider currentUserProvider) : IRequestHandler<GetMyBookingsRequest, GetMyBookingsResponse>
{
    public async ValueTask<GetMyBookingsResponse> Handle(GetMyBookingsRequest request, CancellationToken cancellationToken)
    {
        List<Booking> bookings = await dbContext.Bookings.AsNoTracking()
            .Where(b => b.CustomerId == currentUserProvider.UserId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(cancellationToken);

        // One batched lookup for every distinct unit, not one call per
        // booking - a customer with 100 bookings costs a single cross-
        // module round trip instead of 100.
        IReadOnlyDictionary<Guid, UnitSummary> unitsById = await unitLookup.GetUnitsAsync(
            bookings.Select(b => b.UnitId).Distinct(), cancellationToken);

        return new GetMyBookingsResponse
        {
            Bookings =
            [
                .. bookings.Select(b => new BookingSummary
                {
                    BookingId = b.Id,
                    UnitId = b.UnitId,
                    // A unit archived/deleted after the booking was made
                    // shouldn't break a customer's own booking history -
                    // falls back to an empty name rather than failing the
                    // whole request.
                    UnitName = unitsById.TryGetValue(b.UnitId, out UnitSummary? unit) ? unit.Name : new Dictionary<string, string>(),
                    CheckIn = b.CheckIn,
                    CheckOut = b.CheckOut,
                    GuestCount = b.GuestCount,
                    TotalPrice = b.TotalPrice,
                    Currency = b.Currency,
                    BookingStatus = b.BookingStatus,
                    CreatedAt = b.CreatedAt
                })
            ]
        };
    }
}
