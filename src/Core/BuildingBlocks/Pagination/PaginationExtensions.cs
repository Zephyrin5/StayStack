using Microsoft.EntityFrameworkCore;
namespace BuildingBlocks.Pagination;

public static class PaginationExtensions
{
    public const int MaxPageSize = 100;
    public const int DefaultPageSize = 20;

    // Skip/Take happens on the IQueryable<TEntity> - before mapping to a
    // summary DTO, not after - both for correctness (paginate the actual
    // rows, not an in-memory list) and because it's the only order that
    // lets the count and the page run as two ordinary queries against the
    // database instead of pulling every row over the wire first.
    public static async Task<(List<T> Items, int TotalCount)> ToPagedListAsync<T>(
        this IQueryable<T> query,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        int totalCount = await query.CountAsync(cancellationToken);

        List<T> items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }
}
