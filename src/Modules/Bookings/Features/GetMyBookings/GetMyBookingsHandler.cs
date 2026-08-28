using Bookings.Entities;
using BuildingBlocks.Identity;
using BuildingBlocks.Pagination;
using Catalog.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Bookings.Features.GetMyBookings;

public class GetMyBookingsHandler(
    AppBookingsDbContext dbContext,
    IUnitLookup unitLookup,
    ICurrentUserProvider currentUserProvider) : IRequestHandler<GetMyBookingsRequest, PagedResponse<BookingSummary>>
{
    public async ValueTask<PagedResponse<BookingSummary>> Handle(GetMyBookingsRequest request, CancellationToken cancellationToken)
    {
        // Id as a tiebreaker, not the sort criterion - see docs/adr/0008.
        // CreatedAt alone isn't a total order (two bookings can share a
        // timestamp), so keep the tiebreaker appended if CreatedAt is ever
        // replaced with a different primary sort.
        (List<Booking> bookings, int totalCount) = await dbContext.Bookings.AsNoTracking()
            .Where(b => b.CustomerId == currentUserProvider.UserId)
            .OrderByDescending(b => b.CreatedAt).ThenBy(b => b.Id)
            .ToPagedListAsync(request.Page, request.PageSize, cancellationToken);

        // One batched lookup for every distinct unit, not one call per
        // booking - a customer with 100 bookings costs a single cross-
        // module round trip instead of 100.
        IReadOnlyDictionary<Guid, UnitSummary> unitsById = await unitLookup.GetUnitsAsync(
            bookings.Select(b => b.UnitId).Distinct(), cancellationToken);

        return new PagedResponse<BookingSummary>
        {
            Items =
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
                    TotalPrice = b.TotalPrice.Amount,
                    Currency = b.TotalPrice.Currency,
                    BookingStatus = b.BookingStatus,
                    CreatedAt = b.CreatedAt
                })
            ],
            Page = request.Page,
            PageSize = request.PageSize,
            TotalCount = totalCount
        };
    }
}
