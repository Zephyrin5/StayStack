using Bookings.Entities;
using Bookings.Features.GetHostBookings;
using BuildingBlocks.Exceptions;
using BuildingBlocks.Pagination;
using Catalog.Contracts;
using Hosts.Contracts;
using Mediator;
using Microsoft.EntityFrameworkCore;
namespace Bookings.Features.GetBookingsForHost;

public class GetBookingsForHostHandler(
    AppBookingsDbContext dbContext,
    IUnitLookup unitLookup,
    IHostLookup hostLookup) : IRequestHandler<GetBookingsForHostRequest, PagedResponse<HostBookingSummary>>
{
    public async ValueTask<PagedResponse<HostBookingSummary>> Handle(GetBookingsForHostRequest request, CancellationToken cancellationToken)
    {
        if (!await hostLookup.ExistsAsync(request.HostId, cancellationToken))
        {
            throw new NotFoundException("Host", request.HostId);
        }

        // Bookings has no notion of Property/HostId itself - UnitId is the
        // only link back to Catalog, same cross-module resolution
        // GetHostBookingsHandler already needs for the caller's own host.
        IReadOnlyList<Guid> unitIds = await unitLookup.GetUnitIdsForHostAsync(request.HostId, cancellationToken);

        // Id as a tiebreaker, not deliberate sort criteria - see docs/adr/0008.
        (List<Booking> bookings, int totalCount) = await dbContext.Bookings.AsNoTracking()
            .Where(b => unitIds.Contains(b.UnitId))
            .OrderByDescending(b => b.CreatedAt).ThenBy(b => b.Id)
            .ToPagedListAsync(request.Page, request.PageSize, cancellationToken);

        // One batched lookup for every distinct unit, not one call per
        // booking - same reasoning as GetHostBookingsHandler.
        IReadOnlyDictionary<Guid, UnitSummary> unitsById = await unitLookup.GetUnitsAsync(
            bookings.Select(b => b.UnitId).Distinct(), cancellationToken);

        return new PagedResponse<HostBookingSummary>
        {
            Items =
            [
                .. bookings.Select(b => new HostBookingSummary
                {
                    BookingId = b.Id,
                    UnitId = b.UnitId,
                    UnitName = unitsById.TryGetValue(b.UnitId, out UnitSummary? unit) ? unit.Name : new Dictionary<string, string>(),
                    GuestName = b.GuestName,
                    GuestEmail = b.GuestEmail,
                    GuestPhone = b.GuestPhone,
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
