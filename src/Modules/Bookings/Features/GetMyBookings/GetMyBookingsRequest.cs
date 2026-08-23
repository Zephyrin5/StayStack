using BuildingBlocks.Pagination;
using Mediator;
namespace Bookings.Features.GetMyBookings;

public record GetMyBookingsRequest : IRequest<PagedResponse<BookingSummary>>
{
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
