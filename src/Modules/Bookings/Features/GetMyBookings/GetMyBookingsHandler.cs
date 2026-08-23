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
        // OrderByDescending(CreatedAt) alone isn't a total order - two
        // bookings can share a timestamp - so it's not safe to paginate on
        // by itself: without a tiebreaker, Skip/Take isn't guaranteed to
        // draw the same page boundary on two requests, which can duplicate
        // or skip a row. Id is the tiebreaker convention (see
        // GetPropertiesHandler's equivalent comment), not the sort
        // criteria - keep it appended if CreatedAt is ever replaced with a
        // different primary sort.
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
                    TotalPrice = b.TotalPrice,
                    Currency = b.Currency,
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
