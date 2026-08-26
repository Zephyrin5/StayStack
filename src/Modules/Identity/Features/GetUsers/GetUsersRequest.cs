using BuildingBlocks.Pagination;
using Mediator;
namespace Identity.Features.GetUsers;

public record GetUsersRequest : IRequest<PagedResponse<UserSummary>>
{
    public string? Role { get; init; }
    public int Page { get; init; } = 1;
    public int PageSize { get; init; } = PaginationExtensions.DefaultPageSize;
}
