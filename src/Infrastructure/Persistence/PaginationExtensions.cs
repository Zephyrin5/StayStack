using BuildingBlocks.Exceptions;
using BuildingBlocks.Pagination;
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
        // Before the count, deliberately: a rejected page must cost no database
        // work at all, and CountAsync runs the full filter. It used to run
        // first, so a caller asking for page 20,000,000 paid for the count
        // before anything looked at the offset.
        //
        // The offset is what needed bounding, not the page. page and pageSize
        // are both int and C# arithmetic is unchecked, so (page - 1) * pageSize
        // silently overflowed: page=30000000 with pageSize=100 wrapped
        // 2,999,999,900 to about -1.29e9, and Postgres refuses a negative
        // OFFSET - a 500 reachable from an anonymous query string. Below that
        // threshold nothing overflowed and it was still a scan of two billion
        // rows to discard them all.
        //
        // Here rather than only in the twelve request validators, because this
        // is the one place the arithmetic happens and a thirteenth paged
        // endpoint cannot forget it.
        if (!PaginationDefaults.IsOffsetWithinLimit(page, pageSize))
        {
            throw new ValidationException(
                nameof(page),
                $"Page and PageSize together may not skip past {PaginationDefaults.MaxOffset} results. " +
                "Narrow the search rather than paging deeper.");
        }

        int totalCount = await query.CountAsync(cancellationToken);

        // Safe to narrow: IsOffsetWithinLimit has already bounded this well
        // inside int range.
        int offset = (int)(((long)page - 1) * pageSize);

        List<T> items = await query
            .Skip(offset)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, totalCount);
    }
}
