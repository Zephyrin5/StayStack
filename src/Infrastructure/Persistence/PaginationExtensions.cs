using Microsoft.EntityFrameworkCore;
using Persistence;
namespace Persistence;

/// <summary>
///     Lives here rather than in BuildingBlocks because it is an EF Core
///     extension, and this is the project that owns EF Core. It was the only
///     thing in BuildingBlocks that needed that package - a Core project whose
///     charter is cross-cutting concerns with no infrastructure attached - and
///     the csproj comment justifying the reference there read as an apology
///     for it.
///     <para>
///         Only the query half moved. PagedResponse and
///         PaginationDefaults stay in BuildingBlocks: a response envelope and
///         two page-size bounds are contract shapes, consumed by request DTOs
///         and validators that have no business referencing a persistence
///         assembly.
///     </para>
/// </summary>
public static class PaginationExtensions
{
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
