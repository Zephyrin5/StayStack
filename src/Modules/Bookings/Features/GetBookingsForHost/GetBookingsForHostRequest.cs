using Bookings.Features.GetHostBookings;
using BuildingBlocks.Pagination;
using Mediator;
namespace Bookings.Features.GetBookingsForHost;

// Deliberately not named GetHostBookings - that name is already taken by
// the self-service feature this one parallels. Reuses its HostBookingSummary
// response shape (no reason for a host's own view and an admin's view of
// the same rows to look different) but takes HostId as trusted,
// Administrator-only input instead of resolving it from the caller's own
// token - see docs/adr/0007 and its docs/adr/0013 extension.
public record GetBookingsForHostRequest : IRequest<PagedResponse<HostBookingSummary>>
{
    public Guid HostId { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
