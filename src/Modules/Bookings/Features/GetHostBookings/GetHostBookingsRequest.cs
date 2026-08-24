using BuildingBlocks.Pagination;
using Mediator;
namespace Bookings.Features.GetHostBookings;

public record GetHostBookingsRequest : IRequest<PagedResponse<HostBookingSummary>>
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
