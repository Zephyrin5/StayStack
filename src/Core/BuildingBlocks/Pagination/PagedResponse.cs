namespace BuildingBlocks.Pagination;

// Generic on purpose - a paging envelope has identical semantics for every
// list endpoint that uses it. GetProperties/GetMyProperties/GetMyBookings/
// GetTransactions each close this over their own summary DTO rather than
// inventing their own {Items, Page, PageSize, TotalCount} shape.
public record PagedResponse<T>
{
    public required IReadOnlyList<T> Items { get; init; }
    public required int Page { get; init; }
    public required int PageSize { get; init; }
    public required int TotalCount { get; init; }
}
