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

        // One lookup per distinct unit, not per booking - a customer who
        // booked the same unit more than once shouldn't cost a repeat
        // cross-module call for a name that's already been fetched.
        Dictionary<Guid, UnitSummary?> unitsById = new Dictionary<Guid, UnitSummary?>();
        foreach (Guid unitId in bookings.Select(b => b.UnitId).Distinct())
        {
            unitsById[unitId] = await unitLookup.GetUnitAsync(unitId, cancellationToken);
        }

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
                    UnitName = unitsById[b.UnitId]?.Name ?? new Dictionary<string, string>(),
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
